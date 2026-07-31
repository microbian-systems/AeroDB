using SurrealDb.Net;

namespace AeroDB.Sable;

/// <summary>
/// Serializes embedded transaction lifetimes because embedded providers share
/// transaction state across otherwise independent client sessions.
/// </summary>
internal static class EmbeddedTransactionCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<(SurrealDbTransaction Transaction, IAsyncDisposable? Lease)> BeginAsync(
        ISurrealDbSession session,
        CancellationToken cancellationToken)
    {
        if (!IsEmbedded(session))
            return (await session.BeginTransaction(cancellationToken).ConfigureAwait(false), null);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await session.BeginTransaction(cancellationToken).ConfigureAwait(false);
            return (transaction, new Lease());
        }
        catch
        {
            Gate.Release();
            throw;
        }
    }

    private static bool IsEmbedded(ISurrealDbSession session) =>
        session.Uri?.Scheme is "mem" or "rocksdb" or "surrealkv";

    private sealed class Lease : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();

            return ValueTask.CompletedTask;
        }
    }
}
