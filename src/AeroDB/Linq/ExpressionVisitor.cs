using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using AeroDB.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB;

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
        private readonly SchemaOptions? _schema;
        private Dictionary<ParameterExpression, LinkRegistration> _parameterLinks = new();

        public SurrealExpressionVisitor(SchemaOptions? schema = null)
        {
            _schema = schema;
        }

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
            ["SurrealSeriesFunctions"] = SeriesExpressionHandler.TranslateSeriesFunc,
        };

    internal IReadOnlyDictionary<string, object?> Parameters => _cmdBuilder.Parameters;

    internal string TranslateLinkedWhere(LambdaExpression predicate, IReadOnlyList<LinkRegistration> links)
    {
        if (predicate.Parameters.Count < 2)
            throw new ArgumentException("Linked where predicates must have at least two parameters.", nameof(predicate));

        if (links.Count < predicate.Parameters.Count - 1)
            throw new InvalidOperationException("Linked where predicate has more linked parameters than registered links.");

        _parameterLinks = new Dictionary<ParameterExpression, LinkRegistration>();
        for (var i = 1; i < predicate.Parameters.Count; i++)
            _parameterLinks[predicate.Parameters[i]] = links[i - 1];

        try
        {
            return TranslateConditionCore(predicate.Body, _cmdBuilder);
        }
        finally
        {
            _parameterLinks.Clear();
        }
    }

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
        _parameterLinks.Clear();

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
                    _where.Add(TranslateConditionCore(lambda.Body, _cmdBuilder));
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

    internal static string TranslateCondition(Expression expr, SurrealCommandBuilder builder)
        => new SurrealExpressionVisitor().TranslateConditionCore(expr, builder);

    internal static string TranslateCondition(Expression expr)
        => new SurrealExpressionVisitor().TranslateConditionCore(expr);

    private string TranslateConditionCore(Expression expr, SurrealCommandBuilder builder) => expr switch
    {
        BinaryExpression b => TranslateBinary(b, builder),
        MethodCallExpression m => TranslateMethod(m, builder),
        UnaryExpression u when u.NodeType == ExpressionType.Not
            => $"NOT ({TranslateConditionCore(u.Operand, builder)})",
        MemberExpression m => MemberPath(m),
        _ => ""
    };

    private string TranslateConditionCore(Expression expr) => expr switch
    {
        BinaryExpression b => TranslateBinary(b),
        MethodCallExpression m => TranslateMethod(m),
        UnaryExpression u when u.NodeType == ExpressionType.Not
            => $"NOT ({TranslateConditionCore(u.Operand)})",
        MemberExpression m => MemberPath(m),
        _ => ""
    };

    private string TranslateBinary(BinaryExpression b, SurrealCommandBuilder builder)
    {
        if (TryTranslateCoalescedLinkedTypedAnd(b, builder, out var coalesced))
            return coalesced;

        if (TryTranslateLinkedTypedBinary(b, builder, out var linkedTyped))
            return linkedTyped;

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

    private string TranslateBinary(BinaryExpression b)
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
    /// AeroDB's functions (130+) are simpler: <c>MethodCallExpression → string</c>. A type-based
    /// <c>Dictionary&lt;Type, Func&lt;string, string[], string?&gt;&gt;</c> (<see cref="_handlerDispatch"/>, 9 entries)
    /// plus a name-based fallback (<see cref="_handlerByName"/>, 2 entries for separate-assembly handlers)
    /// provide O(1) stateless dispatch that scales to any number of handlers.
    /// </remarks>
    private string TranslateMethod(MethodCallExpression m, SurrealCommandBuilder builder)
        => TranslateMethodInternal(m, builder);

    private string TranslateMethod(MethodCallExpression m)
        => TranslateMethodInternal(m, null);

    private string TranslateMethodInternal(MethodCallExpression m, SurrealCommandBuilder? builder)
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
            var item = op(m.Arguments[0]);

            // H3 FIXED: captured C# collection → item INSIDE [...]
            if (TryExtractCollection(m.Object!, out var values))
            {
                var formatted = string.Join(", ", values.Select(FormatValue));
                return $"{item} INSIDE [{formatted}]";
            }

            var col = op(m.Object!);
            return $"{col} CONTAINS {item}";
        }

        // ── Enumerable.Any(predicate) → SurrealDB subquery ──
        if (m.Method.Name == "Any" && m.Arguments.Count == 2)
        {
            throw new NotSupportedException(
                ".Any(predicate) on a collection is not directly translatable to SurrealQL. " +
                "Use array::any() or array::all() functions instead, or restructure the query.");
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

    private string Operand(Expression expr, SurrealCommandBuilder builder) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value, builder),
        MemberExpression m when TryExtractCapturedValue(m, out var value) => FormatValue(value, builder),
        MemberExpression m => MemberPath(m),
        NewArrayExpression na => string.Join(", ", na.Expressions.Select(e => Operand(e, builder))),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand, builder),
        _ => TranslateConditionCore(expr, builder)
    };

    private string Operand(Expression expr) => expr switch
    {
        ConstantExpression c => FormatValue(c.Value),
        MemberExpression m when TryExtractCapturedValue(m, out var value) => FormatValue(value),
        MemberExpression m => MemberPath(m),
        NewArrayExpression na => string.Join(", ", na.Expressions.Select(Operand)),
        UnaryExpression u when u.NodeType == ExpressionType.Convert => Operand(u.Operand),
        _ => TranslateConditionCore(expr)
    };

    private string MemberPath(MemberExpression m)
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

        if (m.Expression is ParameterExpression paramExpr)
        {
            if (_parameterLinks.TryGetValue(paramExpr, out var link))
            {
                var linkedField = FieldName(link.TargetType, m.Member.Name);
                return link.FkIsRecordId ? $"{link.FkFieldName}.{linkedField}" : linkedField;
            }

            var propName = m.Member.Name;
            // If this parameter's type has a configured identity, emit native "id" key
            if (_schema?.Mappings.TryGetValue(paramExpr.Type, out var mapping) == true
                && mapping.IdentityProperty == propName)
                return "id";
            return FieldName(paramExpr.Type, propName);
        }
        if (m.Expression is MemberExpression inner)
        {
            if (TryRelationshipPath(m, out var relationshipPath))
                return relationshipPath;

            var innerPath = MemberPath(inner);
            if (IsDateTimeMember(m))
                return DateTimeFunc(m.Member.Name, innerPath);
            return $"{innerPath}.{FieldName(m.Member.DeclaringType, m.Member.Name)}";
        }
        return FieldName(m.Member.DeclaringType, m.Member.Name);
    }

    private bool TryRelationshipPath(MemberExpression member, out string path)
    {
        path = "";
        var members = new List<MemberExpression>();
        Expression? current = member;
        while (current is MemberExpression me)
        {
            members.Add(me);
            current = StripConvert(me.Expression);
        }

        if (current is not ParameterExpression root || members.Count < 2)
            return false;

        members.Reverse();
        var rootMember = members[0];
        if (_schema is null || !_schema.Mappings.TryGetValue(root.Type, out var sourceMapping))
            return false;

        var relationships = sourceMapping.GetRelationshipMappings();
        if (relationships.Count == 0)
            return false;

        var relationship = relationships.FirstOrDefault(r =>
            string.Equals(r.ClrMemberName, rootMember.Member.Name, StringComparison.Ordinal));

        if (relationship is null)
        {
            throw new NotSupportedException(
                $"Cannot translate member access '{root.Type.Name}.{string.Join(".", members.Select(x => x.Member.Name))}'. " +
                $"The member '{root.Type.Name}.{rootMember.Member.Name}' is not configured as an AeroDB relationship.");
        }

        var parts = new List<string> { relationship.StorageFieldName };
        var currentType = relationship.TargetType;
        foreach (var next in members.Skip(1))
        {
            parts.Add(FieldName(currentType, next.Member.Name));
            currentType = GetMemberType(next.Member) ?? currentType;
        }

        path = string.Join(".", parts);
        return true;
    }

    private bool TryTranslateLinkedTypedBinary(BinaryExpression binary, SurrealCommandBuilder builder, out string translated)
    {
        translated = "";
        if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return false;

        if (TryGetLinkedMember(binary.Left, out var leftMember, out var leftLink) && !leftLink.FkIsRecordId)
        {
            var right = Operand(binary.Right, builder);
            translated = BuildTypedLinkSubquery(leftLink, leftMember, binary.NodeType, right);
            return true;
        }

        if (TryGetLinkedMember(binary.Right, out var rightMember, out var rightLink) && !rightLink.FkIsRecordId)
        {
            var left = Operand(binary.Left, builder);
            translated = BuildTypedLinkSubquery(rightLink, rightMember, Flip(binary.NodeType), left);
            return true;
        }

        return false;
    }

    private bool TryTranslateCoalescedLinkedTypedAnd(BinaryExpression binary, SurrealCommandBuilder builder, out string translated)
    {
        translated = "";
        if (binary.NodeType != ExpressionType.AndAlso)
            return false;

        var clauses = new List<BinaryExpression>();
        FlattenAndAlso(binary, clauses);
        if (clauses.Count < 2)
            return false;

        LinkRegistration? commonLink = null;
        var conditions = new List<string>(clauses.Count);

        foreach (var clause in clauses)
        {
            if (!TryBuildLinkedTargetCondition(clause, builder, out var link, out var condition))
                return false;

            if (link.FkIsRecordId)
                return false;

            commonLink ??= link;
            if (!ReferenceEquals(commonLink, link) && commonLink != link)
                return false;

            conditions.Add(condition);
        }

        if (commonLink is null)
            return false;

        translated = string.Join(" AND ", conditions);
        return true;
    }

    private static void FlattenAndAlso(Expression expression, List<BinaryExpression> clauses)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
        {
            FlattenAndAlso(andAlso.Left, clauses);
            FlattenAndAlso(andAlso.Right, clauses);
            return;
        }

        if (expression is BinaryExpression binary)
            clauses.Add(binary);
    }

    private bool TryBuildLinkedTargetCondition(
        BinaryExpression binary,
        SurrealCommandBuilder builder,
        out LinkRegistration link,
        out string condition)
    {
        link = null!;
        condition = "";
        if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return false;

        if (TryGetLinkedMember(binary.Left, out var leftMember, out var leftLink) && !leftLink.FkIsRecordId)
        {
            var right = Operand(binary.Right, builder);
            condition = BuildTargetCondition(leftLink, leftMember, binary.NodeType, right);
            link = leftLink;
            return true;
        }

        if (TryGetLinkedMember(binary.Right, out var rightMember, out var rightLink) && !rightLink.FkIsRecordId)
        {
            var left = Operand(binary.Left, builder);
            condition = BuildTargetCondition(rightLink, rightMember, Flip(binary.NodeType), left);
            link = rightLink;
            return true;
        }

        return false;
    }

    private bool TryGetLinkedMember(Expression expression, out MemberExpression member, out LinkRegistration link)
    {
        expression = StripConvert(expression) ?? expression;
        if (expression is MemberExpression { Expression: ParameterExpression parameter } linkedMember
            && _parameterLinks.TryGetValue(parameter, out var registration))
        {
            member = linkedMember;
            link = registration;
            return true;
        }

        member = null!;
        link = null!;
        return false;
    }

    private string BuildTypedLinkSubquery(
        LinkRegistration link,
        MemberExpression targetMember,
        ExpressionType nodeType,
        string value)
    {
        var field = FieldName(link.TargetType, targetMember.Member.Name);
        var op = nodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"Operator {nodeType}")
        };

        return $"{TypedRecordExpression(link)}.{field} {op} {value}";
    }

    private string BuildTargetCondition(
        LinkRegistration link,
        MemberExpression targetMember,
        ExpressionType nodeType,
        string value)
    {
        var field = FieldName(link.TargetType, targetMember.Member.Name);
        var op = nodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"Operator {nodeType}")
        };

        return $"{TypedRecordExpression(link)}.{field} {op} {value}";
    }

    private static string TypedRecordExpression(LinkRegistration link)
    {
        var fk = link.CastFkToStringForRecordId
            ? $"<string>{link.FkFieldName}"
            : link.FkFieldName;

        return $"type::record(\"{link.TargetTable}\", {fk})";
    }

    private static ExpressionType Flip(ExpressionType nodeType) => nodeType switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => nodeType
    };

    private string FieldName(Type? sourceType, string clrName)
        => sourceType is null
            ? (_schema?.NamingPolicy.FieldName(clrName) ?? clrName)
            : MetadataDispatch.GetFieldName(sourceType, clrName, _schema);

    private static Type? GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
            FieldInfo field => Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType,
            _ => null
        };

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

        if (TryRelationshipPath(m, out var relationshipPath))
            return relationshipPath;

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
                parts.Add(FieldName(me.Member.DeclaringType, me.Member.Name));
                current = StripConvert(me.Expression);
                if (current is ParameterExpression)
                    break;
            }
            parts.Reverse(); // leaf-to-root → root-to-leaf
            return string.Join(".", parts);
        }

        // Single level — unchanged behavior (o.Customer → "Customer")
        return FieldName(m.Member.DeclaringType, m.Member.Name);
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
        Enum e => $"'{Convert.ToInt64(e)}'",
        DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
        _ => val.ToString()!
    };

    private static string FormatValue(object? val, SurrealCommandBuilder builder) => val switch
    {
        null => "NONE",
        bool b => b ? "true" : "false",
        DateTime dt => $"d'{dt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto => $"d'{dto.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'",
        _ => builder.Parameter(val)
    };

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }

    private static bool TryExtractCapturedValue(MemberExpression expr, out object? value)
    {
        value = null;

        if (!IsRootedInConstant(expr))
            return false;

        value = EvaluateCapturedExpression(expr);
        return true;
    }

    private static bool IsRootedInConstant(Expression expr)
    {
        expr = StripConvert(expr) ?? expr;
        while (expr is MemberExpression member)
            expr = StripConvert(member.Expression) ?? member.Expression!;

        return expr is ConstantExpression;
    }

    private static object? EvaluateCapturedExpression(Expression expr)
    {
        expr = StripConvert(expr) ?? expr;
        return expr switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression member => GetMemberValue(EvaluateCapturedExpression(member.Expression!), member.Member),
            _ => null
        };
    }

    private static object? GetMemberValue(object? container, MemberInfo member)
    {
        if (container is null)
            return null;

        return member switch
        {
            FieldInfo field => field.GetValue(container),
            PropertyInfo property => property.GetValue(container),
            _ => null
        };
    }

    /// <summary>
    /// H3: Extracts the actual collection values from a captured C# variable
    /// (constant or closure member) for <c>list.Contains(x.Id)</c> → <c>Id INSIDE [...]</c> translation.
    /// </summary>
    private static bool TryExtractCollection(Expression expr, [NotNullWhen(true)] out List<object?>? values)
    {
        values = null;

        // Case 1: ConstantExpression wrapping an array/list (e.g. new[] { 1, 2, 3 } passed as constant)
        if (expr is ConstantExpression { Value: System.Collections.IEnumerable c and not string })
        {
            values = c.Cast<object?>().ToList();
            return true;
        }

        // Case 2: MemberExpression on a ConstantExpression (captured local variable)
        if (expr is MemberExpression { Expression: ConstantExpression constExpr } me)
        {
            var container = constExpr.Value!;
            var val = me.Member switch
            {
                FieldInfo fi => fi.GetValue(container),
                PropertyInfo pi => pi.GetValue(container),
                _ => null
            };

            if (val is System.Collections.IEnumerable e and not string)
            {
                values = e.Cast<object?>().ToList();
                return values.Count > 0;
            }
        }

        return false;
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
