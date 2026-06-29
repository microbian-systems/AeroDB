using System.Linq.Expressions;
using Dali.Metadata;

namespace Dali;

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
        return source.Where(x => ((ISoftDeleted)x).DeletedAt < cutoff);
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
            throw new NotSupportedException("DeleteAsync requires Dali's SurrealDbQueryable provider.");

        var tableName = MetadataDispatch.GetTableName(typeof(T));
        if (string.IsNullOrEmpty(tableName))
            throw new InvalidOperationException($"Cannot resolve table name for type '{typeof(T).Name}'.");

        // Build the SurrealQL from the expression tree
        var visitor = new SurrealExpressionVisitor();
        var result = visitor.Translate(source.Expression);

        var surql = $"DELETE FROM `{tableName}`";
        if (result.Where.Count > 0)
            surql += " WHERE " + string.Join(" AND ", result.Where);
        surql += ";";

        // Get the provider's session through the internal property
        var session = surrealQueryable.GetSession();
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);
        return response.Count;
    }

    // ── Gap #60-62: CONTAINSALL / CONTAINSANY / CONTAINSNONE / INTERSECTS ──

    private static System.Reflection.MethodInfo GetArrayMethod(string name, Type valueType)
    {
        return typeof(SurrealArrayFunctions).GetMethod(name, [typeof(IEnumerable<>).MakeGenericType(valueType), typeof(IReadOnlyList<>).MakeGenericType(valueType)])!;
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
