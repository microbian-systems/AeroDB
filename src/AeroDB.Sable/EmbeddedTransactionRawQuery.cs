using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

/// <summary>
/// Executes parameterized raw queries on the active session.
/// </summary>
internal static class EmbeddedTransactionRawQuery
{
    public static async Task<SurrealDbResponse> ExecuteAsync(
        ISurrealDbSession session,
        string surql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        if (session is SurrealDbTransaction transaction
            && transaction.Uri?.Scheme is "mem" or "rocksdb" or "surrealkv"
            && parameters is { Count: > 0 })
        {
            foreach (var (key, value) in parameters)
                await transaction.Set(key, value!, cancellationToken).ConfigureAwait(false);

            return await transaction.RawQuery(surql, null, cancellationToken).ConfigureAwait(false);
        }

        return await session.RawQuery(surql, parameters, cancellationToken).ConfigureAwait(false);
    }
}
