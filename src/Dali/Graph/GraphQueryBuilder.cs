using System.Linq.Expressions;
using System.Text;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Dali;

internal sealed class GraphQueryBuilder<TNode> : IGraphQuery<TNode> where TNode : class
{
    private readonly IQuerySession _session;
    private readonly ISurrealDbSession _rawSession;
    private readonly List<GraphStep> _steps = new();
    private int? _depth;
    private int? _depthMin;
    private int? _depthMax;
    private bool _unbounded;
    private bool _hasDepthModifier;
    private string? _shortestPathTarget;
    private bool _returnPath;
    private bool _collectAll;
    private bool _includeOrigin;
    private string[]? _fetchRelations;
    private string? _filterSurql;

    internal GraphQueryBuilder(IQuerySession session, ISurrealDbSession rawSession, string? filterSurql = null)
    {
        _session = session;
        _rawSession = rawSession;
        _filterSurql = filterSurql;
    }

    public IGraphQuery<TTarget> Out<TTarget>(string edgeType) where TTarget : class
        => AddStep<TTarget>(GraphDirection.Out, edgeType);

    public IGraphQuery<TTarget> In<TTarget>(string edgeType) where TTarget : class
        => AddStep<TTarget>(GraphDirection.In, edgeType);

    public IGraphQuery<TTarget> Both<TTarget>(string edgeType) where TTarget : class
        => AddStep<TTarget>(GraphDirection.Both, edgeType);

    public IGraphQuery<GraphNode> OutAny()
        => AddStep<GraphNode>(GraphDirection.Out, null, targetKind: GraphTargetKind.Any);

    public IGraphQuery<GraphNode> InAny()
        => AddStep<GraphNode>(GraphDirection.In, null, targetKind: GraphTargetKind.Any);

    public IGraphQuery<GraphNode> AnyEdge()
        => AddStep<GraphNode>(GraphDirection.Both, null, targetKind: GraphTargetKind.Any);

    public IGraphQuery<TTarget> Out<TTarget>(string[] edgeTypes) where TTarget : class
        => AddStep<TTarget>(GraphDirection.Out, null, edgeTypes, GraphTargetKind.MultiEdge);

    public IGraphQuery<TNode> Depth(int depth)
    {
        _depth = depth;
        _hasDepthModifier = true;
        return this;
    }

    public IGraphQuery<TNode> Depth(int min, int max)
    {
        _depthMin = min;
        _depthMax = max;
        _hasDepthModifier = true;
        return this;
    }

    public IGraphQuery<TNode> Depth()
    {
        _unbounded = true;
        _hasDepthModifier = true;
        return this;
    }

    public IGraphQuery<TNode> ShortestPath(string recordId)
    {
        _shortestPathTarget = recordId;
        _unbounded = true;
        _depth = null;
        _depthMin = null;
        _depthMax = null;
        _hasDepthModifier = true;
        return this;
    }

    public IGraphQuery<TNode> ReturnPath()
    {
        _returnPath = true;
        return this;
    }

    public IGraphQuery<TNode> CollectAll()
    {
        _collectAll = true;
        return this;
    }

    public IGraphQuery<TNode> IncludeIntermediate()
    {
        if (_steps.Count > 0)
            _steps[^1].IncludeIntermediate = true;
        return this;
    }

    public IGraphQuery<TNode> IncludeOrigin()
    {
        _includeOrigin = true;
        return this;
    }

    public IGraphQuery<TNode> Fetch(params string[] relations)
    {
        _fetchRelations = relations;
        return this;
    }

    public IGraphQuery<TNode> Where(Expression<Func<TNode, bool>> predicate)
    {
        // Store the expression as a string representation for the SurrealQL WHERE clause.
        // In a full implementation, this would use the LINQ visitor to translate C# expressions
        // to SurrealQL. For now, we store the expression to be converted by the generator.
        _filterSurql = ExpressionToSurrealQL(predicate);
        return this;
    }

    public async Task<List<TNode>> ToListAsync(CancellationToken ct = default)
    {
        var plan = BuildPlan();
        var sql = GraphSurrealQLGenerator.Generate(plan);
        return await _session.RawQueryAsync<TNode>(sql, null, ct).ConfigureAwait(false);
    }

    public async Task<TNode?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var plan = BuildPlan();
        plan.ReturnPath = false;
        var sql = GraphSurrealQLGenerator.Generate(plan);
        var results = await _session.RawQueryAsync<TNode>(sql, null, ct).ConfigureAwait(false);
        return results.FirstOrDefault();
    }

    public async Task<List<GraphPath>> ToPathListAsync(CancellationToken ct = default)
    {
        var plan = BuildPlan();
        plan.ReturnPath = true;
        var sql = GraphSurrealQLGenerator.Generate(plan);
        var response = await _rawSession.RawQuery(sql, null, ct).ConfigureAwait(false);
        return GraphResultDeserializer.DeserializePaths(response);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var plan = BuildPlan();
        var sql = new StringBuilder();
        sql.Append("SELECT count() FROM (");
        sql.Append(GraphSurrealQLGenerator.Generate(plan).TrimEnd(';'));
        sql.Append(") GROUP ALL;");

        var response = await _rawSession.RawQuery(sql.ToString(), null, ct).ConfigureAwait(false);
        if (response.HasErrors || response.Count == 0)
            return 0;

        var raw = response.GetValue<List<object>>(0);
        if (raw == null || raw.Count == 0)
            return 0;

        var json = System.Text.Json.JsonSerializer.Serialize(raw[0]);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("count", out var countProp))
            return countProp.GetInt32();

        return raw.Count;
    }

    private GraphQueryBuilder<TTarget> AddStep<TTarget>(
        GraphDirection dir,
        string? edgeType,
        string[]? edgeTypes = null,
        GraphTargetKind targetKind = GraphTargetKind.Typed) where TTarget : class
    {
        // Copy current state to new builder
        var builder = new GraphQueryBuilder<TTarget>(_session, _rawSession, _filterSurql);
        builder._steps.AddRange(_steps);
        builder._steps.Add(new GraphStep
        {
            Direction = dir,
            EdgeType = edgeType,
            EdgeTypes = edgeTypes,
            TargetType = typeof(TTarget),
            TargetKind = targetKind
        });
        builder._depth = _depth;
        builder._depthMin = _depthMin;
        builder._depthMax = _depthMax;
        builder._unbounded = _unbounded;
        builder._hasDepthModifier = _hasDepthModifier;
        builder._shortestPathTarget = _shortestPathTarget;
        builder._returnPath = _returnPath;
        builder._collectAll = _collectAll;
        builder._includeOrigin = _includeOrigin;
        builder._fetchRelations = _fetchRelations;
        return builder;
    }

    private GraphQueryPlan BuildPlan()
    {
        return new GraphQueryPlan
        {
            NodeType = typeof(TNode),
            Steps = new List<GraphStep>(_steps),
            Depth = _depth,
            DepthMin = _depthMin,
            DepthMax = _depthMax,
            IsUnboundedDepth = _unbounded,
            ShortestPathTarget = _shortestPathTarget,
            ReturnPath = _returnPath,
            CollectAll = _collectAll,
            IncludeOrigin = _includeOrigin,
            FetchRelations = _fetchRelations,
            FilterSurql = _filterSurql
        };
    }

    /// <summary>
    /// Converts a LINQ expression to a SurrealQL WHERE clause string.
    /// This is a simplified translator for common patterns.
    /// In production, this would use the full LINQ visitor from the query provider.
    /// </summary>
    private static string ExpressionToSurrealQL(Expression<Func<TNode, bool>> predicate)
    {
        // For now, return a simplified representation.
        // This will be enhanced with full LINQ-to-SurrealQL translation in a future phase.
        if (predicate.Body is BinaryExpression binary)
        {
            var left = MemberExpressionToSurrealQL(binary.Left);
            var op = binary.NodeType switch
            {
                ExpressionType.Equal => " = ",
                ExpressionType.NotEqual => " != ",
                ExpressionType.GreaterThan => " > ",
                ExpressionType.GreaterThanOrEqual => " >= ",
                ExpressionType.LessThan => " < ",
                ExpressionType.LessThanOrEqual => " <= ",
                ExpressionType.AndAlso => " AND ",
                ExpressionType.OrElse => " OR ",
                _ => " = "
            };
            var right = ExpressionValueToSurrealQL(binary.Right);

            // For property accesses on the right side, use the field name directly
            if (binary.Right is MemberExpression)
                return $"{left}{op}{right}";

            return $"{left}{op}{right}";
        }

        if (predicate.Body is MethodCallExpression methodCall)
        {
            var member = methodCall.Object as MemberExpression;
            if (member != null)
            {
                var memberName = member.Member.Name;
                var memberField = ToSnakeCase(memberName);
                var arg = methodCall.Arguments.Count > 0
                    ? ExpressionValueToSurrealQL(methodCall.Arguments[0])
                    : "";

                return methodCall.Method.Name switch
                {
                    "Contains" => $"{memberField} CONTAINS {arg}",
                    "StartsWith" => $"{memberField} STARTS WITH {arg}",
                    "EndsWith" => $"{memberField} ENDS WITH {arg}",
                    _ => $"{memberField} = {arg}"
                };
            }
        }

        // Fallback: return the expression as a string
        return predicate.Body.ToString();
    }

    private static string MemberExpressionToSurrealQL(Expression expr)
    {
        if (expr is MemberExpression member)
        {
            return ToSnakeCase(member.Member.Name);
        }

        return expr.ToString();
    }

    private static string ExpressionValueToSurrealQL(Expression expr)
    {
        if (expr is ConstantExpression constant)
        {
            if (constant.Value == null)
                return "NONE";
            if (constant.Value is string s)
                return $"'{s.Replace("'", "\\'")}'";
            if (constant.Value is bool b)
                return b ? "true" : "false";
            if (constant.Value is int or long or float or double or decimal)
                return constant.Value.ToString()!;
            return $"'{constant.Value}'";
        }

        if (expr is MemberExpression member)
        {
            // Try to evaluate the member expression
            try
            {
                var value = Expression.Lambda(member).Compile().DynamicInvoke();
                if (value == null)
                    return "NONE";
                if (value is string s)
                    return $"'{s.Replace("'", "\\'")}'";
                if (value is bool b)
                    return b ? "true" : "false";
                if (value is int or long or float or double or decimal)
                    return value.ToString()!;
                return $"'{value}'";
            }
            catch
            {
                return ToSnakeCase(member.Member.Name);
            }
        }

        return expr.ToString();
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }
}
