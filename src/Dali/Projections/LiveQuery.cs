using System.Runtime.CompilerServices;
using SurrealDb.Net.Models.LiveQuery;

namespace Dali;

/// <summary>
/// Wraps a <see cref="SurrealDbLiveQuery{T}"/> into Dali's <see cref="ILiveQuery{T}"/>.
/// </summary>
internal sealed class LiveQuery<T> : ILiveQuery<T>
{
    private readonly SurrealDbLiveQuery<T> _inner;

    public LiveQuery(SurrealDbLiveQuery<T> inner)
    {
        _inner = inner;
    }

    public async IAsyncEnumerable<T> ResultsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var response in _inner.WithCancellation(ct).ConfigureAwait(false))
        {
            if (response is SurrealDbLiveQueryCreateResponse<T> create)
                yield return create.Result;
            else if (response is SurrealDbLiveQueryUpdateResponse<T> update)
                yield return update.Result;
            else if (response is SurrealDbLiveQueryDeleteResponse<T> delete)
                yield return delete.Result;
        }
    }

    public IAsyncEnumerable<T> CreatedAsync(CancellationToken ct = default)
        => _inner.GetCreatedRecords(ct);

    public IAsyncEnumerable<T> UpdatedAsync(CancellationToken ct = default)
        => _inner.GetUpdatedRecords(ct);

    public IAsyncEnumerable<T> DeletedAsync(CancellationToken ct = default)
        => _inner.GetDeletedRecords(ct);

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _inner.KillAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
