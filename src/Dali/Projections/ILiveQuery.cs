namespace Dali;

/// <summary>
/// Marten API parity: legacy live query interface. Wraps <see cref="Dali.LiveQuery.IDaliLiveQuery{T}"/>
/// for backward compatibility with Marten's <c>IQuerySession.WatchTableAsync</c>,
/// <c>WatchQueryAsync</c>, and <c>WatchStreamAsync</c> methods.
/// Prefer <see cref="Dali.LiveQuery.ILiveQuerySession"/> for new code.
/// </summary>
public interface ILiveQuery<T> : IAsyncDisposable
{
    /// <summary>All create, update, and delete results (no Open/Close events).</summary>
    IAsyncEnumerable<T> ResultsAsync(CancellationToken ct = default);

    /// <summary>Only created records.</summary>
    IAsyncEnumerable<T> CreatedAsync(CancellationToken ct = default);

    /// <summary>Only updated records.</summary>
    IAsyncEnumerable<T> UpdatedAsync(CancellationToken ct = default);

    /// <summary>Only deleted records.</summary>
    IAsyncEnumerable<T> DeletedAsync(CancellationToken ct = default);

    /// <summary>Kill the server-side query.</summary>
    Task StopAsync(CancellationToken ct = default);
}
