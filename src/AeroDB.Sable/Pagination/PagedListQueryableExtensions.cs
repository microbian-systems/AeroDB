namespace AeroDB.Sable.Pagination;

using System.Threading;

/// <summary>
/// Extension methods on <see cref="ISableQueryable{T}"/> for paged queries.
/// Equivalent to Marten's <c>Marten.Pagination.PagedListQueryableExtensions</c>.
/// </summary>
public static class PagedListQueryableExtensions
{
    /// <summary>
    /// Returns a paged list using a single-round-trip Stats query for total count.
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="queryable">The queryable source.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A paged list with total count, page info, and results.</returns>
    public static async Task<IPagedList<T>> ToPagedListAsync<T>(
        this ISableQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
        where T : class
    {
        return await PagedList<T>.CreateAsync(queryable, pageNumber, pageSize, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a paged list. Uses a separate count query when <paramref name="useCountQuery"/>
    /// is true; otherwise uses Stats (single round trip).
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="queryable">The queryable source.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="useCountQuery">Use a separate count query instead of Stats.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A paged list with total count, page info, and results.</returns>
    public static async Task<IPagedList<T>> ToPagedListAsync<T>(
        this ISableQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        bool useCountQuery,
        CancellationToken ct = default)
        where T : class
    {
        return await PagedList<T>.CreateAsync(queryable, pageNumber, pageSize, useCountQuery, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extension method for <see cref="IQueryable{T}"/> that wraps to paged list.
    /// Requires the queryable to be a <see cref="ISableQueryable{T}"/> at runtime.
    /// </summary>
    public static async Task<IPagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
        where T : class
    {
        if (queryable is not ISableQueryable<T> sq)
            throw new InvalidOperationException(
                "ToPagedListAsync is only supported on ISableQueryable<T>. " +
                "Use session.Query<T>() to get a queryable.");
        return await PagedList<T>.CreateAsync(sq, pageNumber, pageSize, ct)
            .ConfigureAwait(false);
    }
}
