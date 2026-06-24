using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Dali.Metadata;
using SurrealDb.Net.Models;

namespace Dali;

public class SurrealExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sb = new();
    private readonly List<string> _where = new();
    private readonly List<string> _orderBy = new();
    private readonly List<string> _groupByColumns = new();
    private readonly List<string> _autoFetchFields = new();
    private int? _limit;
    private int? _skip;
    private string _projection = "*";
    private SurrealCommandBuilder _cmdBuilder = new();

    public SurrealQueryResult Translate(Expression expression)
    {
        _sb.Clear();
        _where.Clear();
        _orderBy.Clear();
        _groupByColumns.Clear();
        _autoFetchFields.Clear();
        _limit = null;
        _skip = null;
        _projection = "*";
        TableName = null;
        _cmdBuilder = new SurrealCommandBuilder();

        Visit(expression);
        return new SurrealQueryResult
        {
            TableName = TableName,
            Where = _where,
            OrderBy = _orderBy,
            GroupBy = [.._groupByColumns],
            Limit = _limit,
            Skip = _skip,
            Projection = _projection,
            FetchFields = [.._autoFetchFields],
            Parameters = _cmdBuilder.Parameters
        };
    }

    public string? TableName { get; private set; }

    /// <summary>
    /// Optional view name override. When set, replaces the type-inferred table name
    /// in the generated SurrealQL. Used by <see cref="ViewQueryExtensions.View{T}"/>
    /// to query pre-computed views.
    /// </summary>
    public string? ViewName { get; set; }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Where":
                Visit(node.Arguments[0]);
                var lambda = StripQuote(node.Arguments[1]) as LambdaExpression;
                if (lambda?.Body is not null)
                    _where.Add(TranslateCondition(lambda.Body, _cmdBuilder));
                break;

            case "OrderBy":
            case "ThenBy":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression ascLambda
                    && ascLambda.Body is MemberExpression ascMember)
                    _orderBy.Add($"{ascMember.Member.Name} ASC");
                break;

            case "OrderByDescending":
            case "ThenByDescending":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression descLambda
                    && descLambda.Body is MemberExpression descMember)
                    _orderBy.Add($"{descMember.Member.Name} DESC");
                break;

            case "Take":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is ConstantExpression takeVal)
                    _limit = (int)takeVal.Value!;
                break;

            case "Skip":
                Visit(node.Arguments[0]);
                if (node.Arguments[1] is ConstantExpression skipVal)
                    _skip = (int)skipVal.Value!;
                break;

            case "Select":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression selLambda)
                {
                    if (selLambda.Body is NewExpression newExpr)
                        _projection = string.Join(", ", newExpr.Arguments.Select(ProjMember));
                    else if (selLambda.Body is MemberInitExpression init)
                    {
                        var parts = new List<string>();
                        foreach (var binding in init.Bindings.OfType<MemberAssignment>())
                        {
                            var expr = binding.Expression;
                            var memberName = binding.Member.Name;

                            // Check if this expression refers to a Record-subclass property
                            bool isRecordProperty = false;
                            if (expr is MemberExpression me)
                            {
                                var propType = me.Member is PropertyInfo pi ? pi.PropertyType : null;
                                var underlyingType = Nullable.GetUnderlyingType(propType!) ?? propType;
                                if (underlyingType is not null && typeof(IRecord).IsAssignableFrom(underlyingType))
                                    isRecordProperty = true;
                            }

                            if (isRecordProperty)
                            {
                                // Use snake_case field name WITHOUT alias (aliases break FETCH).
                                // Compare the snake_case field name against the DTO member name
                                // (case-insensitive) to detect renamed properties.
                                var fieldName = Snake(ProjMember(expr));
                                if (string.Equals(fieldName, memberName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Same name: skip alias (FETCH works), auto-fetch
                                    parts.Add(fieldName);
                                    if (!_autoFetchFields.Contains(fieldName))
                                        _autoFetchFields.Add(fieldName);
                                }
                                else
                                {
                                    // Renamed: use PascalCase alias for CBOR mapping, NO auto-fetch
                                    parts.Add($"{ProjMember(expr)} AS {memberName}");
                                }
                            }
                            else
                            {
                                // Normal behavior: PascalCase ProjMember AS PascalCase memberName
                                parts.Add($"{ProjMember(expr)} AS {memberName}");
                            }
                        }
                        _projection = string.Join(", ", parts);
                    }
                }
                break;

            case "GroupBy":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression gbLambda)
                {
                    if (gbLambda.Body is MemberExpression gbMember)
                        _groupByColumns.Add(gbMember.Member.Name);
                    else if (gbLambda.Body is NewExpression gbNew)
                        foreach (var arg in gbNew.Arguments)
                            if (arg is MemberExpression m)
                                _groupByColumns.Add(m.Member.Name);
                }
                break;

            case "Sum":
            case "Min":
            case "Max":
            case "Average":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression aggLambda
                    && aggLambda.Body is MemberExpression aggMember)
                {
                    var fn = node.Method.Name switch
                    {
                        "Sum" => "math::sum",
                        "Min" => "math::min",
                        "Max" => "math::max",
                        "Average" => "math::mean",
                        _ => node.Method.Name
                    };
                    _projection = $"{fn}({aggMember.Member.Name})";
                }
                break;

            default:
                if (node.Arguments.Count > 0)
                    Visit(node.Arguments[0]);
                break;
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable q)
            TableName = ViewName ?? MetadataDispatch.GetTableName(q.ElementType);
        return node;
    }

    internal static string TranslateCondition(Expression expr, SurrealCommandBuilder builder) => expr switch
    {
        BinaryExpression b => TranslateBinary(b, builder),
        MethodCallExpression m => TranslateMethod(m, builder),
        UnaryExpression u when u.NodeType == ExpressionType.Not
            => $"NOT ({TranslateCondition(u.Operand, builder)})",
        MemberExpression m => m.Member.Name,
        _ => ""
    };

    internal static string TranslateCondition(Expression expr) => expr switch
    {
        BinaryExpression b => TranslateBinary(b),
        MethodCallExpression m => TranslateMethod(m),
        UnaryExpression u when u.NodeType == ExpressionType.Not
            => $"NOT ({TranslateCondition(u.Operand)})",
        MemberExpression m => m.Member.Name,
        _ => ""
    };

    private static string TranslateBinary(BinaryExpression b, SurrealCommandBuilder builder)
    {
        var left = Operand(b.Left, builder);
        var right = Operand(b.Right, builder);
        var op = b.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            ExpressionType.Multiply => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            _ => throw new NotSupportedException($"Operator {b.NodeType}")
        };

        if (b.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return $"({left}) {op} ({right})";

        return $"{left} {op} {right}";
    }

    private static string TranslateBinary(BinaryExpression b)
    {
        var left = Operand(b.Left);
        var right = Operand(b.Right);
        var op = b.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            ExpressionType.Multiply => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            _ => throw new NotSupportedException($"Operator {b.NodeType}")
        };

        if (b.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return $"({left}) {op} ({right})";

        return $"{left} {op} {right}";
    }

    private static string TranslateMethod(MethodCallExpression m, SurrealCommandBuilder builder)
    {
        if (m.Method.DeclaringType == typeof(string))
        {
            var obj = Operand(m.Object!, builder);
            var arg = Operand(m.Arguments[0], builder);
            return m.Method.Name switch
            {
                "Contains" => $"string::contains({obj}, {arg})",
                "StartsWith" => $"string::starts_with({obj}, {arg})",
                "EndsWith" => $"string::ends_with({obj}, {arg})",
                _ => throw new NotSupportedException($"String.{m.Method.Name}")
            };
        }

        if (m.Method.Name == "Contains" && m.Arguments.Count == 1)
        {
            var col = Operand(m.Object!, builder);
            var item = Operand(m.Arguments[0], builder);
            return $"{col} CONTAINS {item}";
        }

        // SurrealFunctions translation
        if (m.Method.DeclaringType == typeof(SurrealFunctions))
        {
            return m.Method.Name switch
            {
                "Score" => $"search::score({Operand(m.Arguments[0], builder)})",
                "VectorDistanceKnn" => "vector::distance::knn()",
                "VectorSimilarityCosine" => $"vector::similarity::cosine({Operand(m.Arguments[0], builder)}, {Operand(m.Arguments[1], builder)})",
                _ => throw new NotSupportedException($"SurrealFunctions.{m.Method.Name}")
            };
        }

        // Geo function dispatch
        if (m.Method.DeclaringType?.Name == "Geo")
        {
            var args = m.Arguments.Select(a => Operand(a, builder)).ToArray();
            var geoResult = GeoExpressionHandler.TranslateGeoFunc(m.Method.Name, args);
            if (geoResult is not null)
                return geoResult;
        }

        // Time function dispatch
        if (m.Method.DeclaringType?.Name == "TimeSeries")
        {
            var args = m.Arguments.Select(a => Operand(a, builder)).ToArray();
            var timeResult = TimeExpressionHandler.TranslateTimeFunc(m.Method.Name, args);
            if (timeResult is not null)
                return timeResult;
        }

        throw new NotSupportedException($"Method {m.Method.Name}");
    }

    private static string TranslateMethod(MethodCallExpression m)
    {
        if (m.Method.DeclaringType == typeof(string))
        {
            var obj = Operand(m.Object!);
            var arg = Operand(m.Arguments[0]);
            return m.Method.Name switch
            {
                "Contains" => $"string::contains({obj}, {arg})",
                "StartsWith" => $"string::starts_with({obj}, {arg})",
                "EndsWith" => $"string::ends_with({obj}, {arg})",
                _ => throw new NotSupportedException($"String.{m.Method.Name}")
            };
        }

        if (m.Method.Name == "Contains" && m.Arguments.Count == 1)
        {
            var col = Operand(m.Object!);
            var item = Operand(m.Arguments[0]);
            return $"{col} CONTAINS {item}";
        }

        if (m.Method.DeclaringType == typeof(SurrealFunctions))
        {
            return m.Method.Name switch
            {
                "Score" => $"search::score({Operand(m.Arguments[0])})",
                "VectorDistanceKnn" => "vector::distance::knn()",
                "VectorSimilarityCosine" => $"vector::similarity::cosine({Operand(m.Arguments[0])}, {Operand(m.Arguments[1])})",
                _ => throw new NotSupportedException($"SurrealFunctions.{m.Method.Name}")
            };
        }

        // Geo function dispatch
        if (m.Method.DeclaringType?.Name == "Geo")
        {
            var args = m.Arguments.Select(a => Operand(a)).ToArray();
            var geoResult = GeoExpressionHandler.TranslateGeoFunc(m.Method.Name, args);
            if (geoResult is not null)
                return geoResult;
        }

        // Time function dispatch
        if (m.Method.DeclaringType?.Name == "TimeSeries")
        {
            var args = m.Arguments.Select(a => Operand(a)).ToArray();
            var timeResult = TimeExpressionHandler.TranslateTimeFunc(m.Method.Name, args);
            if (timeResult is not null)
                return timeResult;
        }

        throw new NotSupportedException($"Method {m.Method.Name}");
    }

    private static string Operand(Expression expr, SurrealCommandBuilder builder) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value, builder),
        MemberExpression m => MemberPath(m),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand, builder),
        _ => TranslateCondition(expr, builder)
    };

    private static string Operand(Expression expr) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value),
        MemberExpression m => MemberPath(m),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand),
        _ => TranslateCondition(expr)
    };

    private static string MemberPath(MemberExpression m)
    {
        if (m.Expression is ParameterExpression)
            return m.Member.Name;
        if (m.Expression is MemberExpression inner)
        {
            var innerPath = MemberPath(inner);
            if (IsDateTimeMember(m))
                return DateTimeFunc(m.Member.Name, innerPath);
            return $"{innerPath}.{m.Member.Name}";
        }
        return m.Member.Name;
    }

    private static bool IsDateTimeMember(MemberExpression m)
        => m.Member.DeclaringType == typeof(DateTime) || m.Member.DeclaringType == typeof(DateTimeOffset);

    private static string DateTimeFunc(string member, string operand) => member switch
    {
        "Year" => $"time::year({operand})",
        "Month" => $"time::month({operand})",
        "Day" => $"time::day({operand})",
        "DayOfWeek" => $"time::wday({operand})",
        "Hour" => $"time::hour({operand})",
        "Minute" => $"time::minute({operand})",
        "Second" => $"time::second({operand})",
        _ => $"{operand}.{member}"
    };

    private static string ProjMember(Expression expr)
    {
        if (expr is not MemberExpression m)
            return "*";

        // Check for chained member access (inner expression is also a MemberExpression)
        // Examples: o.Customer.Name → parts = [Name, Customer], reversed → "Customer.Name"
        //           o.Customer.Address.City → parts = [City, Address, Customer], reversed → "Customer.Address.City"
        // Uses PascalCase to match SurrealDB field naming (consistent with MemberPath).
        if (StripConvert(m.Expression) is MemberExpression)
        {
            var parts = new List<string>();
            Expression? current = expr;
            while (current is MemberExpression me)
            {
                parts.Add(me.Member.Name);
                current = StripConvert(me.Expression);
                if (current is ParameterExpression)
                    break;
            }
            parts.Reverse(); // leaf-to-root → root-to-leaf
            return string.Join(".", parts);
        }

        // Single level — unchanged behavior (o.Customer → "Customer")
        return m.Member.Name;
    }

    /// <summary>
    /// Strips Convert/ConvertChecked UnaryExpression wrappers that LINQ sometimes
    /// inserts (e.g. for lifted-to-nullable conversions). Returns null if expr is null.
    /// </summary>
    private static Expression? StripConvert(Expression? expr)
    {
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } ue)
            expr = ue.Operand;
        return expr;
    }

    private static string FormatValue(object? val) => val switch
    {
        null => "NONE",
        string s => $"'{s.Replace("'", "\\'")}'",
        bool b => b ? "true" : "false",
        int or long or short or byte or float or double or decimal => val.ToString()!,
        DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
        _ => val.ToString()!
    };

    private static string FormatValue(object? val, SurrealCommandBuilder builder) => val switch
    {
        null => "NONE",
        bool b => b ? "true" : "false",
        _ => builder.Parameter(val)
    };

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }

    private static Expression StripQuote(Expression e)
        => e.NodeType == ExpressionType.Quote ? ((UnaryExpression)e).Operand : e;

    /// <summary>
    /// Extracts GROUP BY column names from a key selector expression.
    /// Supports single property (x => x.Category) and anonymous type (x => new { x.Category, x.Region }) selectors.
    /// </summary>
    internal static string[] ExtractGroupByColumns<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression m)
            return [m.Member.Name];
        if (keySelector.Body is NewExpression n)
            return n.Arguments.OfType<MemberExpression>().Select(x => x.Member.Name).ToArray();
        return [];
    }
}

public class SurrealQueryResult
{
    public string? TableName { get; set; }
    public List<string> Where { get; set; } = [];
    public List<string> OrderBy { get; set; } = [];
    public int? Limit { get; set; }
    public int? Skip { get; set; }
    public string Projection { get; set; } = "*";
    public bool GroupAll { get; set; }

    /// <summary>GROUP BY column names for aggregate views.</summary>
    public List<string> GroupBy { get; set; } = [];

    /// <summary>Fields to eager-load via SurrealQL FETCH clause.</summary>
    public List<string> FetchFields { get; set; } = [];

    /// <summary>Inline subquery specifications for forward includes.
    /// These are handled at the provider level using LET-based multi-statement
    /// queries (not emitted by ToSurrealQL).</summary>
    internal List<IncludeSpec>? IncludeSpecs { get; set; }

    /// <summary>Parameter dictionary for safe, parameterized SurrealQL queries.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; set; }
        = new Dictionary<string, object?>();

    public string ToSurrealQL()
    {
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        sb.Append(Projection);
        sb.Append(" FROM `");
        sb.Append(TableName ?? "unknown");
        sb.Append('`');

        if (Where.Count > 0)
        {
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", Where));
        }

        if (GroupAll)
        {
            sb.Append(" GROUP ALL");
        }

        if (GroupBy.Count > 0)
        {
            sb.Append(" GROUP BY ");
            sb.Append(string.Join(", ", GroupBy));
        }

        if (OrderBy.Count > 0)
        {
            sb.Append(" ORDER BY ");
            sb.Append(string.Join(", ", OrderBy));
        }

        if (Limit.HasValue)
        {
            sb.Append(" LIMIT ");
            sb.Append(Limit.Value);
        }

        if (Skip.HasValue)
        {
            sb.Append(" START ");
            sb.Append(Skip.Value);
        }

        if (FetchFields.Count > 0)
        {
            sb.Append(" FETCH ");
            sb.Append(string.Join(", ", FetchFields.Select(f => $"`{f}`")));
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Creates a deep clone of this query result, including copies of all mutable collections.
    /// Thread-safe for concurrent execution of compiled queries.
    /// </summary>
    public SurrealQueryResult Clone()
    {
        return new SurrealQueryResult
        {
            TableName = TableName,
            Where = [..Where],
            OrderBy = [..OrderBy],
            Limit = Limit,
            Skip = Skip,
            Projection = Projection,
            GroupAll = GroupAll,
            GroupBy = [..GroupBy],
            FetchFields = [..FetchFields],
            IncludeSpecs = IncludeSpecs is not null ? [..IncludeSpecs] : null,
            Parameters = new Dictionary<string, object?>(Parameters)
        };
    }
}
