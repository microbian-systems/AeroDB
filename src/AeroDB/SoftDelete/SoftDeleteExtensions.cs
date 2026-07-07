using AeroDB.Metadata;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace AeroDB;

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
        var schema = internalSession.StoreOptions.Schema;
        var table = MetadataDispatch.GetTableName(typeof(T), schema);
        var deletedField = MetadataDispatch.GetFieldName(typeof(T), nameof(ISoftDeleted.Deleted), schema);
        var deletedAtField = MetadataDispatch.GetFieldName(typeof(T), nameof(ISoftDeleted.DeletedAt), schema);

        var surql = $"UPDATE {table}:{recordId} SET {deletedField} = true, {deletedAtField} = d'{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}';";

        await internalSession.Session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }
}
