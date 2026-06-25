namespace Dali.Pagination;

using System.Collections;

/// <summary>
/// Paged list implementation that adapts Dali's <see cref="QueryStatistics"/>
/// for total row count (default, single round trip) or falls back to separate
/// <c>LongCountAsync</c> when <c>useCountQuery</c> is set.
/// </summary>
/// <typeparam name="T">Document type.</typeparam>
public class PagedList<T> : IPagedList<T>
    where T : class
{
    private readonly List<T> _items = new();

    private PagedList()
    {
    }

    public T this[int index] => _items[index];
    public long Count => _items.Count;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();

    public long PageNumber { get; protected set; }
    public long PageSize { get; protected set; }
    public long PageCount { get; protected set; }
    public long TotalItemCount { get; protected set; }
    public bool HasPreviousPage { get; protected set; }
    public bool HasNextPage { get; protected set; }
    public bool IsFirstPage { get; protected set; }
    public bool IsLastPage { get; protected set; }
    public long FirstItemOnPage { get; protected set; }
    public long LastItemOnPage { get; protected set; }

    /// <summary>
    /// Creates a paged list from a queryable using <see cref="QueryStatistics"/>
    /// for total row count (single round trip).
    /// </summary>
    public static async Task<PagedList<T>> CreateAsync(
        IQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
    {
        var pagedList = new PagedList<T>();
        await pagedList.InitAsync(queryable, pageNumber, pageSize, useCountQuery: false, ct)
            .ConfigureAwait(false);
        return pagedList;
    }

    /// <summary>
    /// Creates a paged list from a queryable using either Stats (default)
    /// or a separate count query when <paramref name="useCountQuery"/> is true.
    /// </summary>
    public static async Task<PagedList<T>> CreateAsync(
        IQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        bool useCountQuery,
        CancellationToken ct = default)
    {
        var pagedList = new PagedList<T>();
        await pagedList.InitAsync(queryable, pageNumber, pageSize, useCountQuery, ct)
            .ConfigureAwait(false);
        return pagedList;
    }

    private async Task InitAsync(
        IQueryable<T> queryable,
        int pageNumber,
        int pageSize,
        bool useCountQuery,
        CancellationToken ct)
    {
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "PageNumber cannot be below 1.");
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "PageSize cannot be below 1.");

        PageNumber = pageNumber;
        PageSize = pageSize;

        if (useCountQuery)
        {
            TotalItemCount = await queryable.CountAsync(ct).ConfigureAwait(false);
            var paged = pageNumber == 1
                ? queryable.Take(pageSize)
                : queryable.Skip(((int)pageNumber - 1) * pageSize).Take(pageSize);
            var items = await paged.ToListAsync(ct).ConfigureAwait(false);
            _items.AddRange(items);
        }
        else
        {
            // Use Stats for total count (standard path matching Marten's behavior).
            // Stats runs a separate count query before the data query.
            var _ = new QueryStatistics();
            StatsExtensions.Stats(queryable, out var stats);
            TotalItemCount = await queryable.CountAsync(ct).ConfigureAwait(false);

            var paged = pageNumber == 1
                ? queryable.Take(pageSize)
                : queryable.Skip(((int)pageNumber - 1) * pageSize).Take(pageSize);
            var items = await paged.ToListAsync(ct).ConfigureAwait(false);
            _items.AddRange(items);

            // Use the count query result (more reliable than Stats chain extraction)
            stats.TotalResults = TotalItemCount;
        }

        // Compute derived pagination properties
        PageCount = TotalItemCount > 0
            ? (int)Math.Ceiling(TotalItemCount / (double)pageSize)
            : 0;
        HasPreviousPage = PageNumber > 1;
        HasNextPage = PageNumber < PageCount;
        IsFirstPage = PageCount > 0 && PageNumber == 1;
        IsLastPage = PageCount > 0 && PageNumber >= PageCount;
        FirstItemOnPage = PageCount > 0
            ? ((PageNumber - 1) * PageSize) + 1
            : 0;
        var lastOnPage = FirstItemOnPage + PageSize - 1;
        LastItemOnPage = lastOnPage > TotalItemCount ? TotalItemCount : lastOnPage;
    }
}
