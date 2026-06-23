using System.Collections;
using System.Linq.Expressions;
using SurrealDb.Net.Models;

namespace Dali;

public interface ISurrealDbQueryable<T> : IOrderedQueryable<T>
{
    Task<List<T>> ToListAsync(CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<T?> SingleOrDefaultAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> MinAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> MaxAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);

    /// <summary>
    /// Adds a SurrealQL FETCH clause to eagerly expand a record-typed field.
    ///
    /// <para>SurrealDB's FETCH expands record references inline — the full
    /// referenced document is returned as part of the query result. This is
    /// a <b>server-side operation</b> requiring no additional round trips.</para>
    ///
    /// <para>Only use this on properties typed as <c>RecordId</c> or
    /// <c>RecordId?</c>. Other property types will be silently ignored by
    /// the SurrealDB engine.</para>
    ///
    /// <para>Performance: No additional round trips. The FETCH clause is
    /// appended to the generated SurrealQL. For multiple fields, call
    /// Fetch() multiple times — they accumulate.</para>
    /// </summary>
    /// <param name="property">
    ///   A member expression selecting the record-typed property to expand.
    ///   Example: <c>p => p.TeamId</c>
    /// </param>
    /// <returns>The queryable for chaining.</returns>
    ISurrealDbQueryable<T> Fetch(Expression<Func<T, object?>> property);

    /// <summary>
    /// Eagerly loads related documents using SurrealDB's LET variable for
    /// <b>server-side batch loading in a single round trip</b>.
    ///
    /// <para>The generated SurrealQL uses a LET variable to cache the filtered
    /// main query results, then includes are resolved via subqueries against
    /// the in-memory LET variable — equivalent to PostgreSQL temp tables
    /// but using SurrealDB's native variable system.</para>
    ///
    /// <para><b>Performance:</b> One round trip regardless of how many Includes
    /// are chained. The main WHERE filter is evaluated once by the LET statement.</para>
    ///
    /// <para><b>SurrealQL pattern:</b>
    /// <code>LET $main = (SELECT * FROM source WHERE ...);
    /// SELECT * FROM $main;
    /// SELECT * FROM target WHERE id IN (SELECT VALUE fk FROM $main);</code>
    /// </para>
    /// </summary>
    /// <typeparam name="TProperty">The property type on T (e.g., Guid for an Id reference).</typeparam>
    /// <typeparam name="TInclude">The type of the included document.</typeparam>
    /// <param name="property">
    ///   A member expression selecting the property that holds the foreign key.
    ///   Example: <c>i => i.AssigneeId</c>
    /// </param>
    /// <param name="callback">
    ///   Invoked once for each loaded TInclude document. Use to populate side collections.
    /// </param>
    /// <returns>The queryable for chaining.</returns>
    ISurrealDbQueryable<T> Include<TProperty, TInclude>(
        Expression<Func<T, TProperty>> property,
        Action<TInclude> callback)
        where TInclude : class;

    /// <summary>
    /// Eagerly loads related documents into a dictionary keyed by the property value.
    ///
    /// <para>Like <see cref="Include{TProperty,TInclude}(Expression{Func{T,TProperty}}, Action{TInclude})"/>,
    /// but populates an existing dictionary instead of invoking a callback.
    /// Uses SurrealDB's LET variable for server-side batch loading in a
    /// <b>single round trip</b> regardless of how many Includes are chained.</para>
    /// </summary>
    /// <typeparam name="TKey">The key type (must match TProperty).</typeparam>
    /// <typeparam name="TInclude">The type of the included document.</typeparam>
    /// <param name="key">Member expression selecting the key property.</param>
    /// <param name="dictionary">Dictionary to populate with [key → document] entries.</param>
    /// <returns>The queryable for chaining.</returns>
    ISurrealDbQueryable<T> Include<TKey, TInclude>(
        Expression<Func<T, TKey>> key,
        IDictionary<TKey, TInclude> dictionary)
        where TInclude : class;
}

public class SurrealDbQueryable<T> : ISurrealDbQueryable<T>, IAsyncEnumerable<T>, IOrderedQueryable
{
    private readonly SurrealQueryProvider _provider;

    /// <summary>Fields to eagerly expand via SurrealQL FETCH clause.</summary>
    internal List<string> FetchFields = new();

    /// <summary>Descriptors for post-query client-side eager loading.</summary>
    internal List<IncludeDescriptor> IncludeDescriptors = new();

    /// <summary>
    /// Optional override for the table/view name used in generated SurrealQL.
    /// When set, replaces the type-inferred table name (e.g., for querying
    /// pre-computed views defined with <c>DEFINE TABLE ... AS SELECT ...</c>).
    /// </summary>
    internal string? ViewName { get; set; }

    /// <summary>
    /// Describes a single Include operation — which property to match,
    /// what type to load, and where to dispatch the loaded documents.
    /// </summary>
    internal sealed class IncludeDescriptor
    {
        /// <summary>The property name on T that holds the foreign key.</summary>
        public string PropertyName { get; set; } = "";
        /// <summary>The type of the foreign key property.</summary>
        public Type PropertyType { get; set; } = null!;
        /// <summary>The document type being loaded.</summary>
        public Type IncludeType { get; set; } = null!;
        /// <summary>Optional callback invoked per loaded document.</summary>
        public Delegate? Callback { get; set; }
        /// <summary>Optional dictionary to populate with [key → document] entries.</summary>
        public object? Dictionary { get; set; }
    }

    public SurrealDbQueryable(SurrealQueryProvider provider)
    {
        _provider = provider;
        Expression = Expression.Constant(this);
        ElementType = typeof(T);
    }

    public SurrealDbQueryable(SurrealQueryProvider provider, Expression expression)
    {
        _provider = provider;
        Expression = expression;
        ElementType = typeof(T);
    }

    public Type ElementType { get; }
    public Expression Expression { get; }
    public IQueryProvider Provider => _provider;

    public IEnumerator<T> GetEnumerator()
        => _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors).GetAwaiter().GetResult().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        foreach (var item in await _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors, ct))
            yield return item;
    }

    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors, ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _provider.FirstOrDefaultAsync<T>(Expression, FetchFields, IncludeDescriptors, ct);

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var whereExpr = Expression.Call(
            typeof(Queryable), "Where", [typeof(T)],
            Expression, Expression.Quote(predicate));
        return _provider.FirstOrDefaultAsync<T>(whereExpr, FetchFields, IncludeDescriptors, ct);
    }

    public Task<T?> SingleOrDefaultAsync(CancellationToken ct = default)
        => _provider.SingleOrDefaultAsync<T>(Expression, FetchFields, IncludeDescriptors, ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => _provider.CountAsync(Expression, ct);

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _provider.AnyAsync(Expression, ct);

    public Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var sumExpr = Expression.Call(
            typeof(Queryable), "Sum", [typeof(T)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(sumExpr, fieldName, "math::sum", ct);
    }

    public Task<decimal> MinAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var minExpr = Expression.Call(
            typeof(Queryable), "Min", [typeof(T), typeof(decimal)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(minExpr, fieldName, "math::min", ct);
    }

    public Task<decimal> MaxAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var maxExpr = Expression.Call(
            typeof(Queryable), "Max", [typeof(T), typeof(decimal)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(maxExpr, fieldName, "math::max", ct);
    }

    public Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var avgExpr = Expression.Call(
            typeof(Queryable), "Average", [typeof(T)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(avgExpr, fieldName, "math::mean", ct);
    }

    /// <summary>
    /// Adds a SurrealQL FETCH clause to eagerly expand a record-typed field.
    /// </summary>
    public ISurrealDbQueryable<T> Fetch(Expression<Func<T, object?>> property)
    {
        var memberName = ExtractFieldName(property);
        FetchFields.Add(memberName);
        return this;
    }

    /// <summary>
    /// Eagerly loads related documents by matching on a property value (callback overload).
    /// Uses SurrealDB's LET variable for server-side batch loading in a single round trip.
    /// </summary>
    public ISurrealDbQueryable<T> Include<TProperty, TInclude>(
        Expression<Func<T, TProperty>> property,
        Action<TInclude> callback) where TInclude : class
    {
        var memberName = ExtractFieldName(property);
        IncludeDescriptors.Add(new IncludeDescriptor
        {
            PropertyName = memberName,
            PropertyType = typeof(TProperty),
            IncludeType = typeof(TInclude),
            Callback = callback
        });
        return this;
    }

    /// <summary>
    /// Eagerly loads related documents into a dictionary keyed by the property value.
    /// Uses SurrealDB's LET variable for server-side batch loading in a single round trip.
    /// </summary>
    public ISurrealDbQueryable<T> Include<TKey, TInclude>(
        Expression<Func<T, TKey>> key,
        IDictionary<TKey, TInclude> dictionary) where TInclude : class
    {
        var memberName = ExtractFieldName(key);
        IncludeDescriptors.Add(new IncludeDescriptor
        {
            PropertyName = memberName,
            PropertyType = typeof(TKey),
            IncludeType = typeof(TInclude),
            Dictionary = dictionary
        });
        return this;
    }

    private static string ExtractFieldName<TDelegate>(Expression<TDelegate> selector)
    {
        var body = selector.Body;
        // Unwrap Convert nodes (e.g., value type boxed to object)
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression m)
            return m.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression (e.g., p => p.Price)");
    }
}
