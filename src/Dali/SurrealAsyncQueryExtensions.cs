using System.Linq.Expressions;

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
}
