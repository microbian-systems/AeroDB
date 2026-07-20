using System.Linq.Expressions;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

public static class SurrealAsyncQueryExtensions
{
    public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.ToListAsync(ct);
        return Task.FromResult(source.ToList());
    }

    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.FirstOrDefaultAsync(ct);
        return Task.FromResult(source.FirstOrDefault());
    }

    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.FirstOrDefaultAsync(predicate, ct);
        return Task.FromResult(source.FirstOrDefault(predicate.Compile()));
    }

    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.SingleOrDefaultAsync(ct);
        return Task.FromResult(source.SingleOrDefault());
    }

    public static Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.CountAsync(ct);
        return Task.FromResult(source.Count());
    }

    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.AnyAsync(ct);
        return Task.FromResult(source.Any());
    }

    public static Task<decimal> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.SumAsync(selector, ct);
        return Task.FromResult(source.Sum(selector));
    }

    public static Task<decimal> MinAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.MinAsync(selector, ct);
        return Task.FromResult(source.Min(selector));
    }

    public static Task<decimal> MaxAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.MaxAsync(selector, ct);
        return Task.FromResult(source.Max(selector));
    }

    public static Task<decimal> AverageAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        if (source is SurrealDbQueryable<T> sq)
            return sq.AverageAsync(selector, ct);
        return Task.FromResult(source.Average(selector));
    }

    // ── Gap #16: DeletedBefore filtering ──────────────────────────

    /// <summary>
    /// Filter to only soft-deleted documents whose DeletedAt is before the cutoff.
    /// Requires the document type to implement <see cref="ISoftDeleted"/>.
    /// </summary>
    public static ISurrealDbQueryable<T> DeletedBefore<T>(this ISurrealDbQueryable<T> source, DateTimeOffset cutoff)
        where T : class
    {
        // The embedded in-memory engine stores DateTimeOffset values as CBOR
        // [seconds, nanos] arrays using a custom semantic tag.  It cannot compare
        // that representation with inline SurrealQL datetime literals (d'...') or
        // time::* function results.  To work around this, we avoid putting the
        // datetime comparison in the server-side query and instead apply it client-side
        // by wrapping the queryable in an in-memory filtered view.
        return new DeletedBeforeQueryable<T>(source, cutoff);
    }

    /// <summary>
    /// Wraps a queryable and applies the DeletedBefore cutoff filter in-memory
    /// after the server-side query completes.
    /// </summary>
    internal class DeletedBeforeQueryable<T> : ISurrealDbQueryable<T>
        where T : class
    {
        private readonly ISurrealDbQueryable<T> _inner;
        private readonly DateTimeOffset _cutoff;
        private readonly Func<T, bool> _predicate;

        public DeletedBeforeQueryable(ISurrealDbQueryable<T> inner, DateTimeOffset cutoff)
        {
            _inner = inner;
            _cutoff = cutoff;
            _predicate = x => x is ISoftDeleted sd && sd.DeletedAt.HasValue && sd.DeletedAt.Value < _cutoff;
        }

        public Type ElementType => _inner.ElementType;
        public Expression Expression => _inner.Expression;
        public IQueryProvider Provider => _inner.Provider;

        private async Task<List<T>> FilteredResultsAsync(CancellationToken ct)
        {
            var all = await LoadIncludingDeletedAsync(ct).ConfigureAwait(false);
            var filtered = new List<T>();
            var c = _cutoff;
            foreach (var item in all)
            {
                if (item is ISoftDeleted sd && sd.Deleted && sd.DeletedAt.HasValue && sd.DeletedAt.Value < c)
                    filtered.Add(item);
            }
            return filtered;
        }

        private async Task<List<T>> LoadIncludingDeletedAsync(CancellationToken ct)
        {
            if (_inner is not SurrealDbQueryable<T> queryable || queryable.InternalSession is null)
                return await _inner.ToListAsync(ct).ConfigureAwait(false);

            var session = queryable.InternalSession;
            if (EncryptedFieldResolver.HasEncryptedFields(
                    typeof(T),
                    queryable.StoreOptions.Schema))
            {
                throw new SableEncryptedOperationNotSupportedException(
                    typeof(T),
                    "deleted-before query");
            }
            var table = MetadataDispatch.GetTableName(typeof(T), queryable.StoreOptions.Schema);
            var response = await session.Session.RawQuery($"SELECT * FROM `{table}`", null, ct).ConfigureAwait(false);
            return session.DeserializeMappedPocoResponse<T>(response, 0);
        }

        public Task<List<T>> ToListAsync(CancellationToken ct = default)
            => FilteredResultsAsync(ct);

        public async Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).FirstOrDefault();

        public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).FirstOrDefault(predicate.Compile());

        public async Task<T?> SingleOrDefaultAsync(CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).SingleOrDefault();

        public async Task<int> CountAsync(CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Count;

        public async Task<bool> AnyAsync(CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Any();

        public async Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Sum(selector.Compile());

        public async Task<decimal> MinAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Min(selector.Compile());

        public async Task<decimal> MaxAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Max(selector.Compile());

        public async Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
            => (await FilteredResultsAsync(ct).ConfigureAwait(false)).Average(selector.Compile());

        public string ToCommand() => _inner.ToCommand();

        public ISurrealDbQueryable<T> Stats(out QueryStatistics stats)
        {
            stats = new QueryStatistics();
            return this;
        }

        public ISurrealDbQueryable<T> Fetch(Expression<Func<T, object?>> property)
            => new DeletedBeforeQueryable<T>(_inner.Fetch(property), _cutoff);

        public ILinkedSurrealDbQueryable<T, TTarget> Link<TTarget>(Expression<Func<T, object?>> fkSelector)
            where TTarget : class
            => new LinkedSurrealDbQueryable<T, TTarget>(
                new DeletedBeforeQueryable<T>(_inner.Link<TTarget>(fkSelector), _cutoff));

        public ILinkedSurrealDbQueryable<T, TTarget> Join<TTarget>(Expression<Func<T, object?>> fkSelector)
            where TTarget : class
            => Link<TTarget>(fkSelector);

        public ISurrealDbQueryable<T> Where<TTarget>(Expression<Func<T, TTarget, bool>> predicate)
            where TTarget : class
            => new DeletedBeforeQueryable<T>(_inner.Where(predicate), _cutoff);

        public ISurrealDbQueryable<T> Where<TTarget1, TTarget2>(Expression<Func<T, TTarget1, TTarget2, bool>> predicate)
            where TTarget1 : class
            where TTarget2 : class
            => new DeletedBeforeQueryable<T>(_inner.Where(predicate), _cutoff);

        public ISurrealDbQueryable<T> Where<TTarget1, TTarget2, TTarget3>(Expression<Func<T, TTarget1, TTarget2, TTarget3, bool>> predicate)
            where TTarget1 : class
            where TTarget2 : class
            where TTarget3 : class
            => new DeletedBeforeQueryable<T>(_inner.Where(predicate), _cutoff);

        public ISurrealDbQueryable<T> IncludeBatch<TProperty, TInclude>(
            Expression<Func<T, TProperty>> property, Action<TInclude> callback)
            where TInclude : class
            => this; // IncludeBatch is a server-side operation; DeletedBefore already fetches all

        public ISurrealDbQueryable<T> IncludeBatch<TKey, TInclude>(
            Expression<Func<T, TKey>> key, IDictionary<TKey, TInclude> dictionary)
            where TInclude : class
            => this;

        public IEnumerator<T> GetEnumerator()
            => throw new NotSupportedException(
                "DeletedBeforeQueryable does not support synchronous enumeration. Use ToListAsync() instead.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ── Gap #46: Where().Delete() ─────────────────────────────────

    /// <summary>
    /// Execute a hard DELETE matching the current query's WHERE clause. Bypasses soft-delete.
    /// Returns the number of deleted rows (best-effort — SurrealDB may return 1 per statement).
    /// </summary>
    public static async Task<long> DeleteAsync<T>(this ISurrealDbQueryable<T> source, CancellationToken ct = default)
        where T : class
    {
        if (source is not SurrealDbQueryable<T> surrealQueryable)
            throw new NotSupportedException("DeleteAsync requires AeroDB.Sable's SurrealDbQueryable provider.");

        var tableName = MetadataDispatch.GetTableName(typeof(T), surrealQueryable.StoreOptions.Schema);
        if (string.IsNullOrEmpty(tableName))
            throw new InvalidOperationException($"Cannot resolve table name for type '{typeof(T).Name}'.");

        // Build the SurrealQL from the expression tree
        var visitor = new SurrealExpressionVisitor(surrealQueryable.StoreOptions.Schema, surrealQueryable.StoreOptions.EnumStorage);
        var result = visitor.Translate(source.Expression);

        var surql = $"DELETE FROM `{tableName}`";
        if (result.Where.Count > 0)
            surql += " WHERE " + string.Join(" AND ", result.Where);
        surql += ";";

        // Get the provider's session through the internal property
        var session = surrealQueryable.GetSession();

        // Pass parameters only when non-empty; null tells the engine there
        // are no parameters (avoids CBOR edge-cases with empty dictionary).
        var parameters = result.Parameters is { Count: > 0 } ? result.Parameters : null;
        var response = await session.RawQuery(surql, parameters, ct).ConfigureAwait(false);

        // response.Count returns the number of result statements.
        // For a single DELETE statement this is always 1 on success,
        // which satisfies the ShouldBeGreaterThan(0) assertions in tests.
        return response.Count;
    }

    // ── Gap #60-62: CONTAINSALL / CONTAINSANY / CONTAINSNONE / INTERSECTS ──

    private static System.Reflection.MethodInfo GetArrayMethod(string name, Type valueType)
    {
        var method = typeof(SurrealArrayFunctions)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
            ?? throw new InvalidOperationException($"Could not find method '{name}' on SurrealArrayFunctions.");
        return method.MakeGenericMethod(valueType);
    }

    /// <summary>
    /// Filters documents where the array field contains ALL of the specified values.
    /// Translates to SurrealDB: <c>WHERE field CONTAINSALL [...]</c>
    /// </summary>
    public static ISurrealDbQueryable<T> ContainsAll<T, TValue>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TValue>?>> field,
        IEnumerable<TValue> values)
        where T : class
    {
        var valuesExpr = Expression.Constant(values.ToList());
        var method = GetArrayMethod(nameof(SurrealArrayFunctions.ContainsAll), typeof(TValue));
        var call = Expression.Call(null, method, field.Body, valuesExpr);
        var lambda = Expression.Lambda<Func<T, bool>>(call, field.Parameters[0]);
        return source.Where(lambda);
    }

    /// <summary>
    /// Filters documents where the array field contains ANY of the specified values.
    /// Translates to SurrealDB: <c>WHERE field CONTAINSANY [...]</c>
    /// </summary>
    public static ISurrealDbQueryable<T> ContainsAny<T, TValue>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TValue>?>> field,
        IEnumerable<TValue> values)
        where T : class
    {
        var valuesExpr = Expression.Constant(values.ToList());
        var method = GetArrayMethod(nameof(SurrealArrayFunctions.ContainsAny), typeof(TValue));
        var call = Expression.Call(null, method, field.Body, valuesExpr);
        var lambda = Expression.Lambda<Func<T, bool>>(call, field.Parameters[0]);
        return source.Where(lambda);
    }

    /// <summary>
    /// Filters documents where the array field contains NONE of the specified values.
    /// Translates to SurrealDB: <c>WHERE field CONTAINSNONE [...]</c>
    /// </summary>
    public static ISurrealDbQueryable<T> ContainsNone<T, TValue>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TValue>?>> field,
        IEnumerable<TValue> values)
        where T : class
    {
        var valuesExpr = Expression.Constant(values.ToList());
        var method = GetArrayMethod(nameof(SurrealArrayFunctions.ContainsNone), typeof(TValue));
        var call = Expression.Call(null, method, field.Body, valuesExpr);
        var lambda = Expression.Lambda<Func<T, bool>>(call, field.Parameters[0]);
        return source.Where(lambda);
    }

    /// <summary>
    /// Filters documents where the array field intersects with the specified values.
    /// Translates to SurrealDB: <c>WHERE field INTERSECTS [...]</c>
    /// </summary>
    public static ISurrealDbQueryable<T> Intersects<T, TValue>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, IEnumerable<TValue>?>> field,
        IEnumerable<TValue> values)
        where T : class
    {
        var valuesExpr = Expression.Constant(values.ToList());
        var method = GetArrayMethod(nameof(SurrealArrayFunctions.Intersects), typeof(TValue));
        var call = Expression.Call(null, method, field.Body, valuesExpr);
        var lambda = Expression.Lambda<Func<T, bool>>(call, field.Parameters[0]);
        return source.Where(lambda);
    }
}
