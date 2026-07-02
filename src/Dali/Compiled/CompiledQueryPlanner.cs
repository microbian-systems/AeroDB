using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Dali;

/// <summary>
/// Static planner for interface-based compiled queries (Marten-compatible pattern).
///
/// For each unique compiled query type, the planner builds a <see cref="CompiledPlan"/>
/// once and caches it. The plan stores:
/// <list type="bullet">
///   <item>The <see cref="SurrealQueryResult"/> skeleton with sentinel parameter values.</item>
///   <item>A mapping from SurrealQL parameter keys ($p0, $p1, …) to query property names.</item>
///   <item>Which properties (if any) supply the LIMIT / START values.</item>
/// </list>
///
/// At execution time the skeleton is cloned, property values are read from the
/// query instance, parameters are substituted, and the query is executed.
/// </summary>
public static class CompiledQueryPlanner
{
    private static readonly ConcurrentDictionary<Type, CompiledPlan> _plans = new();

    /// <summary>
    /// Returns the cached plan for the query type, building it
    /// on first access.
    /// </summary>
    public static CompiledPlan GetOrBuildPlan<TDoc, TOut>(ICompiledQuery<TDoc, TOut> query)
        where TDoc : class
    {
        var queryType = query.GetType();
        return _plans.GetOrAdd(queryType, _ => BuildPlan<TDoc, TOut>(query));
    }

    /// <summary>
    /// Executes an interface-based compiled query against the given session.
    /// </summary>
    public static async Task<TOut> QueryAsync<TDoc, TOut>(
        InternalSessionBase session,
        ICompiledQuery<TDoc, TOut> query,
        CancellationToken ct = default)
        where TDoc : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);

        var plan = GetOrBuildPlan<TDoc, TOut>(query);

        // 1. Clone the skeleton — fresh copy for mutation
        var result = plan.SkeletonResult.Clone();

        // 2. Substitute LIMIT / START from actual property values
        if (plan.LimitProperty is not null)
        {
            var limitProp = plan.Properties.First(p => p.Name == plan.LimitProperty);
            result.Limit = (int)limitProp.GetValue(query)!;
        }

        if (plan.SkipProperty is not null)
        {
            var skipProp = plan.Properties.First(p => p.Name == plan.SkipProperty);
            result.Skip = (int)skipProp.GetValue(query)!;
        }

        // 3. Build a new parameter dictionary with live property values
        var newParams = new Dictionary<string, object?>(plan.ParameterMapping.Count);
        foreach (var kvp in plan.ParameterMapping)
        {
            var prop = plan.Properties.First(p => p.Name == kvp.Value);
            newParams[kvp.Key] = prop.GetValue(query);
        }

        result.Parameters = newParams;

        // 4. Apply LIMIT 1 for single-result queries
        if (plan.IsSingleResult)
            result.Limit = 1;

        // 5. Execute
        var surql = result.ToSurrealQL();
        var list = await session.RawQueryAsync<TDoc>(surql, result.Parameters, ct).ConfigureAwait(false);

        // 6. Shape the result
        if (plan.IsSingleResult)
        {
            var first = list is { Count: > 0 } ? list[0] : default;
            return (TOut)(object?)first!;
        }

        return (TOut)(object)list;
    }

    // ── Plan building ─────────────────────────────────────────────────

    private static CompiledPlan BuildPlan<TDoc, TOut>(ICompiledQuery<TDoc, TOut> query)
        where TDoc : class
    {
        var queryType = query.GetType();

        // 1. Discover public readable properties (skip [DaliIgnore])
        var properties = queryType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetCustomAttribute<DaliIgnoreAttribute>() is null)
            .ToArray();

        // 2. Create a fresh instance and assign sentinel values
        var instance = (ICompiledQuery<TDoc, TOut>)Activator.CreateInstance(queryType)!;
        var sentinelMap = new Dictionary<string, object?>(properties.Length);
        AssignSentinels(instance, properties, sentinelMap);

        // 2b. Allow custom sentinel override
        if (instance is IQueryPlanning planning)
            planning.SetUniqueValuesForQueryPlanning();

        // 3. Get the expression tree from QueryIs()
        var expr = instance.QueryIs();

        // 4. Reduce captured member accesses (this.PropertyName) to constants
        var reducer = new QueryInstanceMemberReducer(instance);
        var reducedBody = reducer.Visit(expr.Body);

        // 5. Normalize terminal operators: FirstOrDefault(q, pred) → FirstOrDefault(Where(q, pred))
        var normalizedBody = NormalizeTerminalOperators(reducedBody);

        // 6. Replace the ISurrealDbQueryable<TDoc> parameter with a dummy queryable
        var dummy = CreateDummyQueryable<TDoc>();
        var paramReplacer = new ParameterReplaceVisitor(expr.Parameters[0], Expression.Constant(dummy));
        var visitableExpr = paramReplacer.Visit(normalizedBody);

        // 7. Translate through the SurrealExpressionVisitor
        var visitor = new SurrealExpressionVisitor();
        var queryResult = visitor.Translate(visitableExpr);

        // Ensure table name was resolved
        if (string.IsNullOrEmpty(queryResult.TableName))
            queryResult.TableName = Metadata.MetadataDispatch.GetTableName(typeof(TDoc));

        // 8. Map parameters back to property names by matching sentinel values
        var paramMapping = new Dictionary<string, string>();
        BuildParameterMapping(queryResult, sentinelMap, paramMapping);

        // 9. Detect Limit/Skip property overrides
        string? limitProperty = null;
        string? skipProperty = null;

        if (queryResult.Limit.HasValue)
        {
            var sentinelLimit = queryResult.Limit.Value;
            foreach (var kvp in sentinelMap)
            {
                if (kvp.Value is int intVal && intVal == sentinelLimit)
                {
                    limitProperty = kvp.Key;
                    queryResult.Limit = null; // Clear from skeleton — applied at runtime
                    break;
                }
            }
        }

        if (queryResult.Skip.HasValue)
        {
            var sentinelSkip = queryResult.Skip.Value;
            foreach (var kvp in sentinelMap)
            {
                if (kvp.Value is int intVal && intVal == sentinelSkip)
                {
                    skipProperty = kvp.Key;
                    queryResult.Skip = null; // Clear from skeleton — applied at runtime
                    break;
                }
            }
        }

        // 10. Determine if this is a single-result query
        bool isSingle = typeof(TOut) != typeof(IEnumerable<TDoc>)
                        && !typeof(TOut).IsGenericType
                        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(typeof(TOut));

        return new CompiledPlan
        {
            SkeletonResult = queryResult,
            ParameterMapping = paramMapping,
            LimitProperty = limitProperty,
            SkipProperty = skipProperty,
            Properties = properties,
            IsSingleResult = isSingle
        };
    }

    // ── Sentinel helpers ──────────────────────────────────────────────

    private static int _sentinelCounter;

    private static void AssignSentinels(
        object instance,
        PropertyInfo[] properties,
        Dictionary<string, object?> sentinelMap)
    {
        foreach (var prop in properties)
        {
            var sentinel = CreateSentinel(prop.PropertyType, prop.Name);
            prop.SetValue(instance, sentinel);
            sentinelMap[prop.Name] = sentinel;
        }
    }

    private static object CreateSentinel(Type propType, string propName)
    {
        if (propType == typeof(string))
            return $"__SENTINEL_{propName}__";

        if (propType == typeof(int))
            return Interlocked.Decrement(ref _sentinelCounter);

        if (propType == typeof(long))
            return (long)Interlocked.Decrement(ref _sentinelCounter);

        if (propType == typeof(short))
            return (short)-1;

        if (propType == typeof(byte))
            return (byte)0;

        if (propType == typeof(bool))
            return true;

        if (propType == typeof(double))
            return -1.0;

        if (propType == typeof(float))
            return -1.0f;

        if (propType == typeof(decimal))
            return -1m;

        if (propType == typeof(DateTime))
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        if (propType == typeof(DateTimeOffset))
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Fallback: use string representation
        return $"__SENTINEL_{propName}__";
    }

    private static void BuildParameterMapping(
        SurrealQueryResult queryResult,
        Dictionary<string, object?> sentinelMap,
        Dictionary<string, string> paramMapping)
    {
        foreach (var paramKvp in queryResult.Parameters)
        {
            foreach (var sentinelKvp in sentinelMap)
            {
                if (Equals(paramKvp.Value, sentinelKvp.Value))
                {
                    paramMapping[paramKvp.Key] = sentinelKvp.Key;
                    break;
                }
            }
        }
    }

    // ── Expression tree helpers ────────────────────────────────────────

    /// <summary>
    /// Creates a dummy <see cref="SurrealDbQueryable{TDoc}"/> that supplies
    /// table-name metadata during expression tree visiting. No database
    /// connection is required — no execution occurs during plan building.
    /// </summary>
    private static SurrealDbQueryable<TDoc> CreateDummyQueryable<TDoc>()
        where TDoc : class
    {
        var provider = new SurrealQueryProvider(
            session: null!,
            options: new StoreOptions(),
            tenantId: null);
        return new SurrealDbQueryable<TDoc>(provider);
    }

    /// <summary>
    /// Normalizes terminal operators so their predicates are visited by
    /// the <see cref="SurrealExpressionVisitor"/>.
    ///
    /// <para>For example, <c>Queryable.FirstOrDefault(source, predicate)</c>
    /// is rewritten to <c>Queryable.FirstOrDefault(Queryable.Where(source, predicate))</c>.
    /// The visitor processes the inner <c>Where</c> (generating WHERE clauses)
    /// and safely absorbs the outer <c>FirstOrDefault</c>.</para>
    /// </summary>
    private static Expression NormalizeTerminalOperators(Expression body)
    {
        if (body is MethodCallExpression mce && IsTerminalOperator(mce.Method.Name))
        {
            // Pattern: TerminalOperator(source, predicate, ...) where source is an IQueryable
            if (mce.Arguments.Count >= 2 && IsQueryableSource(mce.Arguments[0]))
            {
                var tArg = mce.Method.GetGenericArguments()[0];
                var source = mce.Arguments[0];
                var predicate = mce.Arguments[1];

                // Wrap source in Where(source, predicate)
                var whereCall = Expression.Call(
                    typeof(Queryable),
                    "Where",
                    [tArg],
                    source,
                    predicate);

                // Rebuild the terminal operator without the predicate argument
                var terminalArgs = new List<Expression> { whereCall };
                for (int i = 2; i < mce.Arguments.Count; i++)
                    terminalArgs.Add(mce.Arguments[i]);

                return Expression.Call(
                    mce.Method.DeclaringType!,
                    mce.Method.Name,
                    mce.Method.GetGenericArguments(),
                    [.. terminalArgs]);
            }
        }

        return body;
    }

    private static bool IsTerminalOperator(string methodName)
        => methodName is "FirstOrDefault" or "SingleOrDefault" or "First" or "Single";

    private static bool IsQueryableSource(Expression expr)
    {
        // The source should be an IQueryable (parameter or method call that returns IQueryable)
        if (expr is ParameterExpression)
            return true;
        if (expr is MethodCallExpression mce)
        {
            var returnType = mce.Method.ReturnType;
            return returnType.IsGenericType &&
                   (returnType.GetGenericTypeDefinition() == typeof(IQueryable<>) ||
                    returnType.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>) ||
                    returnType.GetGenericTypeDefinition() == typeof(ISurrealDbQueryable<>));
        }
        return false;
    }

    // ── Expression visitors ────────────────────────────────────────────

    /// <summary>
    /// Replaces member accesses on captured values (the query instance or
    /// closure objects) with <see cref="ConstantExpression"/> nodes containing
    /// their current values. This allows the <see cref="SurrealExpressionVisitor"/>
    /// to parameterize them correctly instead of treating them as field names.
    /// </summary>
    private sealed class QueryInstanceMemberReducer : ExpressionVisitor
    {
        private readonly object _queryInstance;

        public QueryInstanceMemberReducer(object queryInstance)
        {
            _queryInstance = queryInstance;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // Walk up the member chain to find the innermost expression
            var inner = node.Expression;
            while (inner is MemberExpression me)
                inner = me.Expression;

            // If the innermost expression is a ParameterExpression (e.g. 'x' in x.Name),
            // this is a document field reference — keep as-is.
            // If it's a ConstantExpression, it's a captured value — evaluate and replace.
            if (inner is ConstantExpression)
            {
                var value = EvaluateMemberAccess(node);
                return Expression.Constant(value, node.Type);
            }

            return base.VisitMember(node);
        }

        private static object? EvaluateMemberAccess(Expression expr)
        {
            // Compile and invoke a tiny lambda to get the value
            var lambda = Expression.Lambda<Func<object?>>(
                Expression.Convert(expr, typeof(object)));
            return lambda.Compile()();
        }
    }

    /// <summary>
    /// Replaces all occurrences of a specific <see cref="ParameterExpression"/>
    /// with another expression. Used to substitute the <c>ISurrealDbQueryable&lt;T&gt;</c>
    /// parameter with the dummy queryable constant.
    /// </summary>
    private sealed class ParameterReplaceVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParam;
        private readonly Expression _newExpr;

        public ParameterReplaceVisitor(ParameterExpression oldParam, Expression newExpr)
        {
            _oldParam = oldParam;
            _newExpr = newExpr;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _oldParam ? _newExpr : base.VisitParameter(node);
    }
}
