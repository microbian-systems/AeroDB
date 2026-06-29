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

        /// <summary>
        /// Dictionary dispatch for marker-class handler lookup.
        /// Maps declaring Type → handler function (methodName, args[] → SurrealQL string or null).
        /// Only used for types known at compile time.
        /// </summary>
        private static readonly Dictionary<Type, Func<string, string[], string?>> _handlerDispatch = new()
        {
            [typeof(SurrealFunctions)]       = SearchExpressionHandler.TranslateSearchFunc,
            [typeof(Math)]                   = MathExpressionHandler.TranslateMathFunc,
            [typeof(SurrealStringFunctions)] = StringExpressionHandler.TranslateStringFunc,
            [typeof(SurrealTypeFunctions)]   = TypeExpressionHandler.TranslateTypeFunc,
            [typeof(SurrealCryptoFunctions)] = CryptoExpressionHandler.TranslateCryptoFunc,
            [typeof(SurrealRandFunctions)]  = RandExpressionHandler.TranslateRandFunc,
            [typeof(SurrealArrayFunctions)]  = ArrayExpressionHandler.TranslateArrayFunc,
            [typeof(SurrealSessionFunctions)] = SessionExpressionHandler.TranslateSessionFunc,
            [typeof(SurrealMetaFunctions)]    = MetaExpressionHandler.TranslateMetaFunc,
            [typeof(SurrealObjectFunctions)]  = ObjectExpressionHandler.TranslateObjectFunc,
            [typeof(SurrealHttpFunctions)]    = HttpExpressionHandler.TranslateHttpFunc,
            [typeof(SurrealBytesFunctions)]   = BytesExpressionHandler.TranslateBytesFunc,
            [typeof(SurrealDurationFunctions)] = DurationExpressionHandler.TranslateDurationFunc,
        };

        /// <summary>
        /// Name-based dispatch for handlers whose types come from separate assemblies
        /// and cannot be referenced by typeof() at compile time (Geo, TimeSeries).
        /// </summary>
        private static readonly Dictionary<string, Func<string, string[], string?>> _handlerByName = new()
        {
            ["Geo"]        = GeoExpressionHandler.TranslateGeoFunc,
            ["TimeSeries"] = TimeExpressionHandler.TranslateTimeFunc,
        };

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
                if (StripQuote(node.Arguments[1]) is LambdaExpression ascLambda)
                {
                    if (ascLambda.Body is MemberExpression ascMember)
                        _orderBy.Add($"{MemberPath(ascMember)} ASC");
                    else if (ascLambda.Body is MethodCallExpression ascMethod)
                        _orderBy.Add($"{TranslateMethodInternal(ascMethod, _cmdBuilder)} ASC");
                }
                break;

            case "OrderByDescending":
            case "ThenByDescending":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression descLambda)
                {
                    if (descLambda.Body is MemberExpression descMember)
                        _orderBy.Add($"{MemberPath(descMember)} DESC");
                    else if (descLambda.Body is MethodCallExpression descMethod)
                        _orderBy.Add($"{TranslateMethodInternal(descMethod, _cmdBuilder)} DESC");
                }
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
                    else if (gbLambda.Body is MethodCallExpression gbMethod)
                        _groupByColumns.Add(TranslateMethodInternal(gbMethod, _cmdBuilder));
                    else if (gbLambda.Body is NewExpression gbNew)
                        foreach (var arg in gbNew.Arguments)
                        {
                            if (arg is MemberExpression m)
                                _groupByColumns.Add(m.Member.Name);
                            else if (arg is MethodCallExpression gbNewMethod)
                                _groupByColumns.Add(TranslateMethodInternal(gbNewMethod, _cmdBuilder));
                        }
                }
                break;

            case "Sum":
            case "Min":
            case "Max":
            case "Average":
                Visit(node.Arguments[0]);
                if (StripQuote(node.Arguments[1]) is LambdaExpression aggLambda)
                {
                    var fn = node.Method.Name switch
                    {
                        "Sum" => "math::sum",
                        "Min" => "math::min",
                        "Max" => "math::max",
                        "Average" => "math::mean",
                        _ => node.Method.Name
                    };

                    if (aggLambda.Body is MemberExpression aggMember)
                        _projection = $"{fn}({aggMember.Member.Name})";
                    else if (aggLambda.Body is MethodCallExpression aggMethod)
                    {
                        try
                        {
                            var translated = TranslateMethodInternal(aggMethod, _cmdBuilder);
                            _projection = $"{fn}({translated})";
                        }
                        catch (NotSupportedException)
                        {
                            _projection = $"{fn}(*)";
                        }
                    }
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

    /// <summary>
    /// Translates a method call inside a WHERE/ORDER BY/projection expression into a SurrealQL fragment.
    /// </summary>
    /// <remarks>
    /// <b>Extension point for SurrealDB built-in functions (string::, math::, crypto::, array::, etc.):</b>
    ///
    /// Two strategies for exposing functions to C# LINQ expressions:
    ///
    /// <b>1. Intercept existing .NET methods</b> — when a .NET method maps naturally to a SurrealQL function.
    ///    Example: <c>s.Contains("x")</c> → <c>string::contains(s, 'x')</c>, <c>Math.Abs(x)</c> → <c>math::abs(x)</c>.
    ///    Add a branch checking <c>m.Method.DeclaringType</c> for <c>typeof(string)</c>, <c>typeof(Math)</c>, etc.
    ///
    /// <b>2. Marker static class</b> — when no .NET equivalent exists.
    ///    Define a static class with methods that throw <c>NotSupportedException</c> (used only for expression-tree identity).
    ///    Example: <c>SurrealTypes.IsString(x)</c> → <c>type::is::string(x)</c>.
    ///    Add a branch checking <c>m.Method.DeclaringType == typeof(SurrealTypes)</c>.
    ///
    /// <b>Handler class pattern</b> (preferred for 5+ functions in a category):
    ///    Create a <c>static string? Translate(string methodName, string[] args)</c> handler.
    ///    The visitor dispatches by <c>DeclaringType?.Name</c>; the handler returns null for unrecognized methods.
    ///    See <see cref="GeoExpressionHandler"/> and <see cref="TimeExpressionHandler"/> for canonical examples.
    ///
    /// <b>Scaling note:</b> Marten uses <c>IMethodCallParser</c> (interface + registration + caching) for 30+ parser types.
    /// Dali's functions (130+) are simpler: <c>MethodCallExpression → string</c>. A type-based
    /// <c>Dictionary&lt;Type, Func&lt;string, string[], string?&gt;&gt;</c> (<see cref="_handlerDispatch"/>, 9 entries)
    /// plus a name-based fallback (<see cref="_handlerByName"/>, 2 entries for separate-assembly handlers)
    /// provide O(1) stateless dispatch that scales to any number of handlers.
    /// </remarks>
    private static string TranslateMethod(MethodCallExpression m, SurrealCommandBuilder builder)
        => TranslateMethodInternal(m, builder);

    private static string TranslateMethod(MethodCallExpression m)
        => TranslateMethodInternal(m, null);

    private static string TranslateMethodInternal(MethodCallExpression m, SurrealCommandBuilder? builder)
    {
        Func<Expression, string> op = builder is null
            ? Operand
            : e => Operand(e, builder);

        // ── String interceptions (standard .NET) ──
        if (m.Method.DeclaringType == typeof(string))
        {
            // Static string methods (m.Object is null, e.g. string.Concat)
            if (m.Object is null)
            {
                return m.Method.Name switch
                {
                    "Concat" when m.Arguments.Count >= 2
                        => $"string::concat({string.Join(", ", m.Arguments.Select(a => op(a)))})",
                    _ => throw new NotSupportedException($"String.{m.Method.Name}")
                };
            }

            var obj = op(m.Object!);
            var arg = m.Arguments.Count > 0 ? op(m.Arguments[0]) : null;
            return m.Method.Name switch
            {
                "Contains" => $"string::contains({obj}, {arg})",
                "StartsWith" => $"string::starts_with({obj}, {arg})",
                "EndsWith" => $"string::ends_with({obj}, {arg})",
                "Trim" when m.Arguments.Count == 0 => $"string::trim({obj})",
                "ToUpper" or "ToUpperInvariant" when m.Arguments.Count == 0 => $"string::uppercase({obj})",
                "ToLower" or "ToLowerInvariant" when m.Arguments.Count == 0 => $"string::lowercase({obj})",
                "TrimStart" when m.Arguments.Count == 0 => $"string::trim({obj})",
                "Replace" when m.Arguments.Count == 2 => $"string::replace({obj}, {op(m.Arguments[0])}, {op(m.Arguments[1])})",
                "Substring" when m.Arguments.Count == 1 => $"string::slice({obj}, {op(m.Arguments[0])})",
                "Substring" when m.Arguments.Count == 2 => $"string::slice({obj}, {op(m.Arguments[0])}, {op(m.Arguments[1])})",
                _ => throw new NotSupportedException($"String.{m.Method.Name}")
            };
        }

        // ── Collection containment ──
        if (m.Method.Name == "Contains" && m.Arguments.Count == 1)
        {
            var col = op(m.Object!);
            var item = op(m.Arguments[0]);
            return $"{col} CONTAINS {item}";
        }

        // ── Type-based handler dispatch ──
        if (m.Method.DeclaringType is { } dt && _handlerDispatch.TryGetValue(dt, out var handler))
        {
            var args = m.Arguments.Select(a => op(a)).ToArray();
            var result = handler(m.Method.Name, args);
            if (result is not null)
                return result;
        }

        // ── Name-based handler dispatch (for types in separate assemblies) ──
        if (m.Method.DeclaringType?.Name is { } name && _handlerByName.TryGetValue(name, out var nameHandler))
        {
            var args = m.Arguments.Select(a => op(a)).ToArray();
            var result = nameHandler(m.Method.Name, args);
            if (result is not null)
                return result;
        }

        throw new NotSupportedException($"Method {m.Method.Name}");
    }

    private static string Operand(Expression expr, SurrealCommandBuilder builder) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value, builder),
        MemberExpression m => MemberPath(m),
        NewArrayExpression na => string.Join(", ", na.Expressions.Select(e => Operand(e, builder))),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand, builder),
        _ => TranslateCondition(expr, builder)
    };

    private static string Operand(Expression expr) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value),
        MemberExpression m => MemberPath(m),
        NewArrayExpression na => string.Join(", ", na.Expressions.Select(Operand)),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand),
        _ => TranslateCondition(expr)
    };

    private static string MemberPath(MemberExpression m)
    {
        // Handle string.Length property → string::length(expr)
        if (m.Member.DeclaringType == typeof(string) && m.Member.Name == "Length")
        {
            var target = m.Expression switch
            {
                MemberExpression me => MemberPath(me),
                ParameterExpression pe => pe.Name!,
                _ => ""
            };
            return $"string::length({target})";
        }

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

    private string ProjMember(Expression expr)
    {
        // Strip ALL Convert wrappers (nullable lifting, boxing, numeric widening)
        expr = StripConvert(expr) ?? expr;

        // Handle method calls in projections (e.g., Math.Abs(x.Value), x.Name.Trim())
        if (expr is MethodCallExpression mce)
        {
            try
            {
                return TranslateMethodInternal(mce, _cmdBuilder);
            }
            catch (NotSupportedException)
            {
                // TODO: Log warning when method translation silently falls back to *
                return "*";
            }
        }

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
