using System.Runtime.CompilerServices;
using AeroDB.LiveQuery;
using SurrealDb.Net.Models.LiveQuery;

namespace AeroDB;

/// <summary>
/// Marten API parity adapter: bridges the old <see cref="ILiveQuery{T}"/> interface
/// over the new <see cref="IDaliLiveQuery{T}"/> subsystem.
/// Used internally by <c>WatchTableAsync</c>, <c>WatchQueryAsync</c>, and
/// <c>WatchStreamAsync</c> compatibility methods.
/// </summary>
internal sealed class LegacyLiveQueryAdapter<T> : ILiveQuery<T>
{
    private readonly IDaliLiveQuery<T> _inner;

    public LegacyLiveQueryAdapter(IDaliLiveQuery<T> inner)
    {
        _inner = inner;
    }

    public async IAsyncEnumerable<T> ResultsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in _inner.Changes(ct)
            .Where(c => c.Action is DaliLiveAction.Created
                                     or DaliLiveAction.Updated
                                     or DaliLiveAction.Deleted)
            .ConfigureAwait(false))
        {
            if (change.Document is not null)
                yield return change.Document;
        }
    }

    public async IAsyncEnumerable<T> CreatedAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in _inner.Changes(ct)
            .Where(c => c.Action == DaliLiveAction.Created)
            .ConfigureAwait(false))
        {
            if (change.Document is not null)
                yield return change.Document;
        }
    }

    public async IAsyncEnumerable<T> UpdatedAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in _inner.Changes(ct)
            .Where(c => c.Action == DaliLiveAction.Updated)
            .ConfigureAwait(false))
        {
            if (change.Document is not null)
                yield return change.Document;
        }
    }

    public async IAsyncEnumerable<T> DeletedAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in _inner.Changes(ct)
            .Where(c => c.Action == DaliLiveAction.Deleted)
            .ConfigureAwait(false))
        {
            if (change.Document is not null)
                yield return change.Document;
        }
    }

    public Task StopAsync(CancellationToken ct = default)
        => _inner.StopAsync(ct);

    public async ValueTask DisposeAsync()
        => await _inner.DisposeAsync();
}
