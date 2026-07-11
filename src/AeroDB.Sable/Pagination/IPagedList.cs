namespace AeroDB.Sable.Pagination;

using System.Collections.Generic;

/// <summary>
/// Interface for paged list results.
/// Equivalent to Marten's <c>Marten.Pagination.IPagedList&lt;T&gt;</c>.
/// </summary>
/// <typeparam name="T">Document type.</typeparam>
public interface IPagedList<out T> : IEnumerable<T>
{
    T this[int index] { get; }
    long Count { get; }
    long PageNumber { get; }
    long PageSize { get; }
    long PageCount { get; }
    long TotalItemCount { get; }
    bool HasPreviousPage { get; }
    bool HasNextPage { get; }
    bool IsFirstPage { get; }
    bool IsLastPage { get; }
    long FirstItemOnPage { get; }
    long LastItemOnPage { get; }
}
