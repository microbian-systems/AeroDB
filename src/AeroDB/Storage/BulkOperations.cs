using System.Text.Json;
using System.Text.Json.Serialization;
using AeroDB.Metadata;

namespace AeroDB;

public static class BulkOperations
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Bulk inserts entities using a single SurrealDB batch INSERT statement per chunk,
    /// instead of per-row Store + SaveChangesAsync. This reduces N round-trips to
    /// ceil(N/batchSize) round-trips.
    /// </summary>
    public static async Task<int> BulkInsertAsync<T>(
        this IDocumentSession session, IReadOnlyList<T> entities,
        int batchSize = 100, CancellationToken ct = default) where T : class
    {
        if (entities.Count == 0) return 0;
        var table = MetadataDispatch.GetTableName(typeof(T));
        var inserted = 0;

        for (var i = 0; i < entities.Count; i += batchSize)
        {
            var batch = entities.Skip(i).Take(batchSize).ToList();

            // Serialize each entity to a JSON content object for SurrealDB's array INSERT syntax.
            // SurrealQL: INSERT INTO table [{...}, {...}, ...]
            var jsonItems = batch.Select(e => JsonSerializer.Serialize(e, JsonOptions)).ToArray();
            var jsonArray = "[" + string.Join(",", jsonItems) + "]";
            var sql = $"INSERT INTO {table} {jsonArray}";

            await session.ExecuteSqlAsync(sql, null, ct).ConfigureAwait(false);
            inserted += batch.Count;
        }

        return inserted;
    }

    /// <summary>Bulk deletes entities by their IDs in configurable batches.</summary>
    public static async Task<int> BulkDeleteAsync<T>(
        this IDocumentSession session, IReadOnlyList<string> ids,
        int batchSize = 100, CancellationToken ct = default) where T : class
    {
        if (ids.Count == 0) return 0;
        var table = MetadataDispatch.GetTableName(typeof(T));
        var deleted = 0;
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var batch = ids.Skip(i).Take(batchSize).ToList();
            foreach (var id in batch)
            {
                var entity = await session.LoadAsync<T>(id, ct).ConfigureAwait(false);
                if (entity is not null)
                {
                    session.Delete(entity);
                    deleted++;
                }
            }
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return deleted;
    }
}
