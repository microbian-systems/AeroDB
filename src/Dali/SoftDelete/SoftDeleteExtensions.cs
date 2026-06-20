using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Extension methods for soft-deleting documents in SurrealDB.
/// </summary>
public static class SoftDeleteExtensions
{
    /// <summary>
    /// Soft deletes a document by marking it as deleted instead of removing the record.
    /// The document is updated immediately via SurrealQL to set <c>deleted</c> and <c>deleted_at</c>.
    /// </summary>
    /// <typeparam name="T">The document type, must extend <see cref="Record"/> and implement <see cref="ISoftDeleted"/>.</typeparam>
    /// <param name="session">The document session.</param>
    /// <param name="recordId">The string identifier of the record (e.g., "abc123").</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SoftDeleteAsync<T>(this IDocumentSession session, string recordId, CancellationToken ct = default)
        where T : Record, ISoftDeleted
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        var internalSession = (InternalSessionBase)session;
        var table = internalSession.Snake(typeof(T).Name);

        // Field names must be PascalCase to match the C# property names used by the CBOR serializer.
        var surql = $"UPDATE {table}:{recordId} SET Deleted = true, DeletedAt = d'{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}';";

        await internalSession.Session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }
}
