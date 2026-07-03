using System.Linq.Expressions;
using System.Text;
using Dali.Metadata;
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

    public IGraphQuery<TTarget> Out<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord
    {
        var edgeName = MetadataDispatch.GetTableName(typeof(TEdge));
        return AddStep<TTarget>(GraphDirection.Out, edgeName);
    }

    public IGraphQuery<TTarget> In<TTarget>(string edgeType) where TTarget : class
        => AddStep<TTarget>(GraphDirection.In, edgeType);

    public IGraphQuery<TTarget> In<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord
    {
        var edgeName = MetadataDispatch.GetTableName(typeof(TEdge));
        return AddStep<TTarget>(GraphDirection.In, edgeName);
    }

    public IGraphQuery<TTarget> Both<TTarget>(string edgeType) where TTarget : class
        => AddStep<TTarget>(GraphDirection.Both, edgeType);

    public IGraphQuery<TTarget> Both<TTarget, TEdge>() where TTarget : class where TEdge : EdgeRecord
    {
        var edgeName = MetadataDispatch.GetTableName(typeof(TEdge));
        return AddStep<TTarget>(GraphDirection.Both, edgeName);
    }

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
        if (plan.Steps.Count == 0)
        {
            var sql = GraphSurrealQLGenerator.Generate(plan);
            return await _session.RawQueryAsync<TNode>(sql, null, ct).ConfigureAwait(false);
        }

        var runtimeSql = GenerateRuntimeValueQuery(plan);
        var nested = await _session.RawQueryAsync<List<TNode>>(runtimeSql, null, ct).ConfigureAwait(false);
        var results = nested.SelectMany(static items => items).ToList();
        return plan.CollectAll ? DeduplicateByRecordId(results) : results;
    }

    public async Task<TNode?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var results = await ToListAsync(ct).ConfigureAwait(false);
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
        var results = await ToListAsync(ct).ConfigureAwait(false);
        return results.Count;
    }

    private static string GenerateRuntimeValueQuery(GraphQueryPlan plan)
    {
        var baseTable = MetadataDispatch.GetTableName(plan.NodeType);
        var expression = BuildRuntimeTraversalExpression(plan);
        var sql = new StringBuilder()
            .Append("SELECT VALUE ")
            .Append(expression)
            .Append(" FROM `")
            .Append(baseTable)
            .Append('`');

        if (!string.IsNullOrWhiteSpace(plan.FilterSurql))
            sql.Append(" WHERE ").Append(plan.FilterSurql);

        if (plan.FetchRelations is { Length: > 0 })
            sql.Append(" FETCH ").Append(string.Join(", ", plan.FetchRelations));

        sql.Append(';');
        return sql.ToString();
    }

    private static string BuildRuntimeTraversalExpression(GraphQueryPlan plan)
    {
        if (plan.Steps.Count == 1 && plan.Steps[0].Direction == GraphDirection.Both && !HasDepthRange(plan))
        {
            var step = plan.Steps[0];
            return $"array::concat({BuildDirectionalStep(step, GraphDirection.In)}.*, {BuildDirectionalStep(step, GraphDirection.Out)}.*)";
        }

        var depthExpressions = BuildDepthExpressions(plan).ToList();
        if (depthExpressions.Count > 1)
            return $"array::concat({string.Join(", ", depthExpressions)})";

        return depthExpressions[0];
    }

    private static IEnumerable<string> BuildDepthExpressions(GraphQueryPlan plan)
    {
        var (minDepth, maxDepth) = GetDepthRange(plan);
        for (var depth = minDepth; depth <= maxDepth; depth++)
            yield return BuildRepeatedTraversalExpression(plan.Steps, depth);
    }

    private static (int Min, int Max) GetDepthRange(GraphQueryPlan plan)
    {
        if (plan.DepthMin.HasValue && plan.DepthMax.HasValue)
            return (Math.Max(1, plan.DepthMin.Value), Math.Max(plan.DepthMin.Value, plan.DepthMax.Value));

        if (plan.Depth.HasValue)
            return (1, Math.Max(1, plan.Depth.Value));

        if (plan.IsUnboundedDepth)
            return (1, 10);

        return (1, 1);
    }

    private static bool HasDepthRange(GraphQueryPlan plan)
        => plan.Depth.HasValue || plan.DepthMin.HasValue || plan.DepthMax.HasValue || plan.IsUnboundedDepth;

    private static string BuildRepeatedTraversalExpression(IReadOnlyList<GraphStep> steps, int repeatCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < repeatCount; i++)
        {
            foreach (var step in steps)
                sb.Append(BuildStep(step));
        }
        sb.Append(".*");
        return sb.ToString();
    }

    private static string BuildStep(GraphStep step)
        => step.Direction == GraphDirection.Both
            ? BuildDirectionalStep(step, GraphDirection.Both)
            : BuildDirectionalStep(step, step.Direction);

    private static string BuildDirectionalStep(GraphStep step, GraphDirection direction)
    {
        var arrow = direction switch
        {
            GraphDirection.In => "<-",
            GraphDirection.Both => "<->",
            _ => "->"
        };

        var sb = new StringBuilder();
        if (step.EdgeTypes is { Length: > 0 })
            sb.Append(arrow).Append('(').Append(string.Join(", ", step.EdgeTypes)).Append(')');
        else if (step.EdgeType is not null)
            sb.Append(arrow).Append(step.EdgeType);
        else
            sb.Append(arrow).Append('?');

        if (step.IncludeIntermediate)
            sb.Append("(+)");

        var targetTable = step.TargetType is null ? "?" : $"`{MetadataDispatch.GetTableName(step.TargetType)}`";
        sb.Append(arrow).Append(targetTable);
        return sb.ToString();
    }

    private static List<TNode> DeduplicateByRecordId(List<TNode> results)
    {
        var seen = new HashSet<string>();
        var deduped = new List<TNode>(results.Count);

        foreach (var result in results)
        {
            var key = result switch
            {
                SurrealDb.Net.Models.Record record when record.Id is not null => record.Id.ToString()!,
                _ => result.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            if (seen.Add(key))
                deduped.Add(result);
        }

        return deduped;
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
                var memberField = memberName;
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
            return member.Member.Name;
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
            return member.Member.Name;
            }
        }

        return expr.ToString();
    }

}
