using Dali.Metadata;

namespace Dali;

public static class BulkOperations
{
    /// <summary>Bulk inserts entities into a table in configurable batches.</summary>
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
            // Use INSERT for batch creation
            foreach (var entity in batch)
                session.Store(entity);
            inserted += batch.Count;
        }
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
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
