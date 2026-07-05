using AeroDB.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB;

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
        var idList = ids?.ToList() ?? throw new ArgumentNullException(nameof(ids));
        if (idList.Count == 0) return Array.Empty<T>();

        var tableName = MetadataDispatch.GetTableName(typeof(T));

        // Build IN clause with full record IDs (table:id format)
        var inClause = string.Join(", ", idList.Select(id => $"'{tableName}:{id}'"));
        var sql = $"SELECT * FROM {tableName} WHERE id IN [{inClause}];";

        try
        {
            var results = await session.RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
            return results?.Count > 0 ? results.AsReadOnly() : Array.Empty<T>();
        }
        catch
        {
            return Array.Empty<T>();
        }
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
        var idList = ids?.ToList() ?? throw new ArgumentNullException(nameof(ids));
        return await LoadManyAsync<T>(session, idList.Select(ToRecordIdString), ct).ConfigureAwait(false);
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

        // For numeric IDs, format as table:id
        var recordIds = ids.Select(id => $"{tableName}:{id}");
        var inClause = string.Join(", ", recordIds.Select(id => $"'{id}'"));
        var sql = $"SELECT * FROM {tableName} WHERE id IN [{inClause}];";

        try
        {
            var results = await session.RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
            return results?.Count > 0 ? results.AsReadOnly() : Array.Empty<T>();
        }
        catch
        {
            return Array.Empty<T>();
        }
    }

    /// <summary>
    /// Converts a <see cref="RecordId"/> to its string representation (<c>table:id</c>).
    /// </summary>
    private static string ToRecordIdString(RecordId rid) => rid switch
    {
        RecordIdOf<string> s => $"{s.Table}:{s.Id}",
        RecordIdOf<long> l => $"{l.Table}:{l.Id}",
        RecordIdOf<int> i => $"{i.Table}:{i.Id}",
        _ => throw new ArgumentException(
            $"Unsupported RecordId type '{rid.GetType().Name}'. Expected RecordIdOf<string>, RecordIdOf<long>, or RecordIdOf<int>.",
            nameof(rid))
    };
}
