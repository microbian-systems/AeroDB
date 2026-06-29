using System.Collections;
using System.Linq.Expressions;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Dali;

using Dali.Metadata;

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
    /// Returns the generated SurrealQL for this query without executing it.
    /// Useful for debugging and logging.
    /// </summary>
    string ToCommand();

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
    ISurrealDbQueryable<T> IncludeBatch<TProperty, TInclude>(
        Expression<Func<T, TProperty>> property,
        Action<TInclude> callback)
        where TInclude : class;

    /// <summary>
    /// Eagerly loads related documents into a dictionary keyed by the property value.
    ///
    /// <para>Like <see cref="IncludeBatch{TProperty,TInclude}(Expression{Func{T,TProperty}}, Action{TInclude})"/>,
    /// but populates an existing dictionary instead of invoking a callback.
    /// Uses SurrealDB's LET variable for server-side batch loading in a
    /// <b>single round trip</b> regardless of how many Includes are chained.</para>
    /// </summary>
    /// <typeparam name="TKey">The key type (must match TProperty).</typeparam>
    /// <typeparam name="TInclude">The type of the included document.</typeparam>
    /// <param name="key">Member expression selecting the key property.</param>
    /// <param name="dictionary">Dictionary to populate with [key → document] entries.</param>
    /// <returns>The queryable for chaining.</returns>
    ISurrealDbQueryable<T> IncludeBatch<TKey, TInclude>(
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

    /// <summary>Specifications for inline subquery-based forward includes.</summary>
    internal List<IncludeSpec> IncludeSpecs = new();

    /// <summary>Filter predicates applied in-memory after includes are loaded.</summary>
    internal List<FilterIncludeSpec> FilterIncludeSpecs = new();

    /// <summary>Returns the underlying SurrealDB session for raw query execution.</summary>
    internal ISurrealDbSession GetSession() => ((SurrealQueryProvider)Provider).Session;

    /// <summary>
    /// Optional override for the table/view name used in generated SurrealQL.
    /// When set, replaces the type-inferred table name (e.g., for querying
    /// pre-computed views defined with <c>DEFINE TABLE ... AS SELECT ...</c>).
    /// </summary>
    internal string? ViewName { get; set; }

    /// <summary>
    /// When set, the query will also return total row count in a single round trip.
    /// Populated after <see cref="ToListAsync"/> completes.
    /// </summary>
    internal QueryStatistics? QueryStats { get; set; }

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

    public string ToCommand() => _provider.ToCommand(Expression);

    public IEnumerator<T> GetEnumerator()
        => _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs, null, default).GetAwaiter().GetResult().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        foreach (var item in await _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs, null, ct))
            yield return item;
    }

    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => _provider.ToListAsync<T>(Expression, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs,
            QueryStats ?? SurrealQueryProvider.ExtractQueryStats(Expression), ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _provider.FirstOrDefaultAsync<T>(Expression, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs, ct);

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var whereExpr = Expression.Call(
            typeof(Queryable), "Where", [typeof(T)],
            Expression, Expression.Quote(predicate));
        return _provider.FirstOrDefaultAsync<T>(whereExpr, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs, ct);
    }

    public Task<T?> SingleOrDefaultAsync(CancellationToken ct = default)
        => _provider.SingleOrDefaultAsync<T>(Expression, FetchFields, IncludeDescriptors, IncludeSpecs, FilterIncludeSpecs, ct);

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
    public ISurrealDbQueryable<T> IncludeBatch<TProperty, TInclude>(
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
    public ISurrealDbQueryable<T> IncludeBatch<TKey, TInclude>(
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

// ── Extension methods ────────────────────────────────────────────────

public static class SurrealDbQueryableExtensions
{
    /// <summary>
    /// Projects each element of a sequence into a new form, preserving the
    /// <see cref="ISurrealDbQueryable{T}"/> type for further chaining (e.g., Fetch, Include).
    /// </summary>
    /// <typeparam name="T">The source entity type.</typeparam>
    /// <typeparam name="TResult">The result element type of the projection.</typeparam>
    /// <param name="source">The queryable source.</param>
    /// <param name="selector">A projection function to apply to each element.</param>
    /// <returns>An <see cref="ISurrealDbQueryable{TResult}"/> for further chaining.</returns>
    public static ISurrealDbQueryable<TResult> Select<T, TResult>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, TResult>> selector)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (selector is null) throw new ArgumentNullException(nameof(selector));

        var expr = Expression.Call(
            typeof(Queryable),
            "Select",
            [typeof(T), typeof(TResult)],
            source.Expression,
            Expression.Quote(selector));

        return (ISurrealDbQueryable<TResult>)source.Provider.CreateQuery<TResult>(expr);
    }

    /// <summary>
    /// Filters a sequence of values based on a predicate, preserving the
    /// <see cref="ISurrealDbQueryable{T}"/> type for further chaining (e.g., Fetch, Include, ToCommand).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="source">The queryable source.</param>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <returns>An <see cref="ISurrealDbQueryable{T}"/> for further chaining.</returns>
    public static ISurrealDbQueryable<T> Where<T>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, bool>> predicate)
        where T : class
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));

        var expr = Expression.Call(
            typeof(Queryable),
            "Where",
            [typeof(T)],
            source.Expression,
            Expression.Quote(predicate));

        return (ISurrealDbQueryable<T>)source.Provider.CreateQuery<T>(expr);
    }

    /// <summary>
    /// Eagerly loads a typed <c>record&lt;T&gt;</c> property via an inline subquery.
    /// The subquery is embedded in the SELECT clause, using <c>$parent</c> to reference
    /// the parent row's foreign key field.
    ///
    /// <para>Generated SurrealQL pattern:
    /// <c>SELECT *, (SELECT * FROM `target` WHERE id = $parent.fk LIMIT 1)[0] AS Property FROM `source`</c>
    /// </para>
    ///
    /// <para>This is a single-round-trip operation — no separate queries are executed.
    /// Unlike <c>Fetch()</c>, this does not require the SurrealDB server to support
    /// the FETCH clause and works with typed <c>record&lt;T&gt;</c> properties.</para>
    /// </summary>
    /// <typeparam name="T">The source entity type.</typeparam>
    /// <typeparam name="TInclude">The included document type (must inherit from <c>Record</c>).</typeparam>
    /// <param name="source">The queryable source.</param>
    /// <param name="property">
    ///   A member expression selecting the typed record property to include.
    ///   Example: <c>o => o.Customer</c>
    /// </param>
    /// <returns>The queryable for chaining.</returns>
    public static ISurrealDbQueryable<T> Include<T, TInclude>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, TInclude?>> property)
        where T : class
        where TInclude : class
    {
        if (source is not SurrealDbQueryable<T> queryable)
            throw new InvalidOperationException("Include is only supported on SurrealDbQueryable<T>.");

        if (property.Body is not MemberExpression memberExpr)
            throw new ArgumentException("Expression must be a member access (e.g., o => o.Customer).");

        var propName = memberExpr.Member.Name;
        var fkField = propName.ToLowerInvariant();
        var targetTable = Dali.Metadata.MetadataDispatch.GetTableName(typeof(TInclude));

        queryable.IncludeSpecs.Add(new IncludeSpec
        {
            PropertyName = propName,
            TargetTable = targetTable,
            ForeignKeyField = fkField,
            IncludeType = typeof(TInclude),
            IsSingle = true
        });

        return queryable;
    }

    /// <summary>
    /// Eagerly loads a collection of child records where the child has a foreign key
    /// pointing back to the parent <c>T</c>. Uses SurrealDB's LET variable for
    /// <b>server-side batch loading in a single round trip</b>.
    ///
    /// <para>Generated SurrealQL pattern:
    /// <c>LET $main = (SELECT * FROM source WHERE ...);
    /// SELECT * FROM $main;
    /// SELECT * FROM `child` WHERE `fkField` IN (SELECT VALUE [id|Id] FROM $main);</c>
    /// </para>
    ///
    /// <para>Unlike <see cref="Include{T,TInclude}(ISurrealDbQueryable{T}, Expression{Func{T,TInclude?}})"/>,
    /// which is forward (FK on parent), this is reverse (FK on child). The child records
    /// are collected into a <c>List&lt;TChild&gt;</c> and set on the collection property.</para>
    /// </summary>
    /// <typeparam name="T">The source entity type (must implement <c>IRecord</c> to have an Id).</typeparam>
    /// <typeparam name="TChild">The child record type.</typeparam>
    /// <param name="source">The queryable source.</param>
    /// <param name="property">
    ///   A member expression selecting the collection property on T.
    ///   Example: <c>o => o.Items</c>
    /// </param>
    /// <param name="foreignKey">
    ///   The foreign key field on the child table (e.g., <c>"order"</c>).
    ///   Both the C# property name and SurrealQL field name.
    /// </param>
    /// <returns>The queryable for chaining.</returns>
    public static ISurrealDbQueryable<T> IncludeReverse<T, TChild>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TChild>?>> property,
        string foreignKey)
        where T : class
        where TChild : class
    {
        if (source is not SurrealDbQueryable<T> queryable)
            throw new InvalidOperationException("IncludeReverse is only supported on SurrealDbQueryable<T>.");

        if (property.Body is not MemberExpression memberExpr)
            throw new ArgumentException("Expression must be a member access (e.g., o => o.Items).");

        var propName = memberExpr.Member.Name;
        var targetTable = MetadataDispatch.GetTableName(typeof(TChild));

        var spec = new IncludeSpec
        {
            PropertyName = propName,
            TargetTable = targetTable,
            ForeignKeyField = foreignKey,  // FK field on the child table
            IncludeType = typeof(TChild),
            IsSingle = false,   // collection
            IsForward = false   // reverse
        };

        // Pre-compute the parent ID field name for SurrealQL generation.
        // Record types use "id" (RecordId), Entity types use "Id" (typed property).
        var parentIsRecord = typeof(IRecord).IsAssignableFrom(typeof(T));
        spec.ParentIdField = parentIsRecord ? "id" : "Id";

        queryable.IncludeSpecs.Add(spec);

        return queryable;
    }

    /// <summary>
    /// Filters parent documents based on a predicate applied to their included
    /// child collection <b>in-memory</b>, after includes are fully loaded.
    ///
    /// <para>Only parent documents whose child collection satisfies the predicate
    /// are returned. Unlike a WHERE clause, this filter runs on the client side
    /// and can use any LINQ expression on the child collection (e.g.,
    /// <c>.Any()</c>, <c>.All()</c>, <c>.Count()</c>).</para>
    ///
    /// <para>Must be used after <see cref="IncludeReverse{T,TChild}"/> on the same
    /// property to have effect. The filter does not affect which children are
    /// loaded — it only controls which parent documents are included in results.</para>
    /// </summary>
    /// <typeparam name="T">The source entity type (must implement <c>IRecord</c>).</typeparam>
    /// <typeparam name="TChild">The child record type.</typeparam>
    /// <param name="source">The queryable source.</param>
    /// <param name="property">
    ///   A member expression selecting the collection property on T.
    ///   Example: <c>o => o.Items</c>
    /// </param>
    /// <param name="filter">
    ///   A predicate applied to the child collection. Returning <c>true</c> keeps
    ///   the parent in the result set.
    ///   Example: <c>items => items.Any(i => i.Price > 50)</c>
    /// </param>
    /// <returns>The queryable for chaining.</returns>
    public static ISurrealDbQueryable<T> FilterInclude<T, TChild>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TChild>>> property,
        Expression<Func<IEnumerable<TChild>, bool>> filter)
        where T : class
        where TChild : class
    {
        if (source is not SurrealDbQueryable<T> queryable)
            throw new InvalidOperationException("FilterInclude is only supported on SurrealDbQueryable<T>.");

        if (property.Body is not MemberExpression memberExpr)
            throw new ArgumentException("Expression must be a member access (e.g., o => o.Items).");

        queryable.FilterIncludeSpecs.Add(new FilterIncludeSpec
        {
            PropertyName = memberExpr.Member.Name,
            Filter = filter
        });

        return queryable;
    }
}
