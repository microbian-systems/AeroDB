namespace AeroDB.Sable;

public static class PagedListCompatibilityExtensions
{
    public static Task<Pagination.IPagedList<T>> ToPagedListAsync<T>(
        this ISableQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
        where T : class
        => Pagination.PagedListQueryableExtensions.ToPagedListAsync(queryable, pageNumber, pageSize, ct);

    public static Task<Pagination.IPagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
        where T : class
        => Pagination.PagedListQueryableExtensions.ToPagedListAsync(queryable, pageNumber, pageSize, ct);
}
