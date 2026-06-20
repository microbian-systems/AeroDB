namespace Dali;

/// <summary>
/// A live query subscription to a SurrealDB table or query.
/// Disposing stops the underlying SurrealDB live query.
/// </summary>
public interface ILiveQuery<T> : IAsyncDisposable
{
    /// <summary>All results (create, update, delete) as they arrive.</summary>
    IAsyncEnumerable<T> ResultsAsync(CancellationToken ct = default);

    /// <summary>Only newly created records.</summary>
    IAsyncEnumerable<T> CreatedAsync(CancellationToken ct = default);

    /// <summary>Only updated records.</summary>
    IAsyncEnumerable<T> UpdatedAsync(CancellationToken ct = default);

    /// <summary>Only deleted records.</summary>
    IAsyncEnumerable<T> DeletedAsync(CancellationToken ct = default);

    /// <summary>Stops the live query. Same as disposal but allows explicit control.</summary>
    Task StopAsync(CancellationToken ct = default);
}
