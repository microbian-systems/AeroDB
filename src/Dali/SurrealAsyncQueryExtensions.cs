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
}
