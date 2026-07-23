using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>
/// Batch document loading extensions for <see cref="IQuerySession"/>.
/// </summary>
public static class LoadManyExtensions
{
    /// <summary>
    /// Load multiple documents by their string record IDs in a single round-trip.
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="ids">String record IDs to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Loaded documents in the order they were returned by the database.</returns>
    public static async Task<IReadOnlyList<T>> LoadManyAsync<T>(
        this IQuerySession session, IEnumerable<string> ids, CancellationToken ct = default)
        where T : class
    {
        var idList = ids?.Cast<object>().ToList() ?? throw new ArgumentNullException(nameof(ids));
        return await LoadManyByIdentityAsync<T>(session, idList, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Load multiple documents by their <see cref="RecordId"/> values.
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="ids">Record IDs to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Loaded documents in the order they were returned by the database.</returns>
    public static async Task<IReadOnlyList<T>> LoadManyAsync<T>(
        this IQuerySession session, IEnumerable<RecordId> ids, CancellationToken ct = default)
        where T : class
    {
        var idList = ids?.Cast<object>().ToList() ?? throw new ArgumentNullException(nameof(ids));
        return await LoadManyByIdentityAsync<T>(session, idList, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Load multiple documents by numeric IDs (e.g., Snowflake IDs) scoped to a specific table.
    /// </summary>
    /// <typeparam name="T">Document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="tableName">The target table name.</param>
    /// <param name="numericIds">Numeric IDs to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Loaded documents in the order they were returned by the database.</returns>
    public static async Task<IReadOnlyList<T>> LoadManyAsync<T>(
        this IQuerySession session, string tableName, IEnumerable<long> numericIds, CancellationToken ct = default)
        where T : class
    {
        var ids = numericIds?.ToList() ?? throw new ArgumentNullException(nameof(numericIds));
        if (ids.Count == 0) return Array.Empty<T>();

        var inClause = string.Join(
            ", ",
            ids.Select(id => DocumentIdentityResolver.FormatRecordIdLiteral(tableName, id)));
        var sql = $"SELECT * FROM {tableName} WHERE id IN [{inClause}];";

        try
        {
            var results = await session.RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
            return results?.Count > 0 ? results.AsReadOnly() : Array.Empty<T>();
        }
        catch (SableEncryptionException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<T>();
        }
    }

    internal static async Task<IReadOnlyList<T>> LoadManyByIdentityAsync<T>(
        IQuerySession session,
        IEnumerable<object> ids,
        CancellationToken ct = default)
        where T : class
    {
        var idList = ids?.ToList() ?? throw new ArgumentNullException(nameof(ids));
        if (idList.Count == 0) return Array.Empty<T>();

        var schema = session is InternalSessionBase internalSession
            ? internalSession.StoreOptions.Schema
            : new StoreOptions().Schema;
        var tableName = MetadataDispatch.GetTableName(typeof(T), schema);
        var literals = new List<string>(idList.Count);
        foreach (var id in idList)
        {
            var normalizedId = id is RecordId
                ? id
                : DocumentIdentityResolver.NormalizeForDocumentType(typeof(T), id, schema);
            if (!DocumentIdentityResolver.TryCreate(normalizedId, tableName, out var identity))
                continue;
            literals.Add(identity.Literal);
        }

        if (literals.Count == 0) return Array.Empty<T>();

        var sql = $"SELECT * FROM {tableName} WHERE id IN [{string.Join(", ", literals)}];";
        try
        {
            var results = await session.RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
            return results.Count > 0 ? results.AsReadOnly() : Array.Empty<T>();
        }
        catch (SableEncryptionException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<T>();
        }
    }
}
