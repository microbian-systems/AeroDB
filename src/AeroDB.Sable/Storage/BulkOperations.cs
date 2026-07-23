using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

public static class BulkOperations
{
    /// <summary>
    /// Bulk inserts entities using a single SurrealDB batch INSERT statement per chunk,
    /// instead of per-row Store + SaveChangesAsync. This reduces N round-trips to
    /// ceil(N/batchSize) round-trips.
    /// </summary>
    public static async Task<int> BulkInsertAsync<T>(
        this IDocumentSession session, IReadOnlyList<T> entities,
        int batchSize = 100, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entities);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (entities.Count == 0) return 0;
        if (session is not DocumentSession documentSession)
        {
            throw new InvalidOperationException(
                "Bulk insert requires a Sable-owned document session.");
        }
        InternalSessionBase internalSession = documentSession;

        var options = internalSession.StoreOptions;
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), options.Schema))
        {
            throw new SableEncryptedOperationNotSupportedException(
                typeof(T),
                "bulk insert");
        }
        foreach (var entity in entities)
        {
            ArgumentNullException.ThrowIfNull(entity);
            var runtimeType = entity.GetType();
            if (runtimeType != typeof(T))
            {
                if (EncryptedFieldResolver.HasEncryptedFields(runtimeType, options.Schema))
                {
                    throw new SableEncryptedOperationNotSupportedException(
                        runtimeType,
                        "polymorphic bulk insert");
                }

                throw new InvalidOperationException(
                    $"Polymorphic bulk insert is not supported. Declared type " +
                    $"'{typeof(T).FullName}' received runtime type '{runtimeType.FullName}'.");
            }
        }
        PrepareTenantValues(internalSession, entities, options);

        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(
            typeof(T), options.Schema);
        var targetSession = await internalSession
            .GetSessionForSchemaAsync(mappedDatabase, ct)
            .ConfigureAwait(false);
        var inserted = 0;

        for (var i = 0; i < entities.Count; i += batchSize)
        {
            var batch = entities.Skip(i).Take(batchSize).ToList();

            var sql = string.Join(
                Environment.NewLine,
                batch.Select(entity => documentSession.BuildBulkCreateStatement(entity, table)));

            var response = await targetSession.RawQuery(sql, null, ct).ConfigureAwait(false);
            response.EnsureAllOks();
            inserted += batch.Count;
        }

        return inserted;
    }

    private static void PrepareTenantValues<T>(
        InternalSessionBase session,
        IReadOnlyList<T> entities,
        StoreOptions options)
        where T : class
    {
        if (options.TenancyStyle == TenancyStyle.DatabasePerTenant)
            return;

        var mapping = options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var hasTenantId = MetadataDispatch.HasTenantId(typeof(T));
        if (mapping?.IsMultiTenanted == true && string.IsNullOrWhiteSpace(session.TenantId))
        {
            throw new InvalidOperationException(
                $"A tenant-scoped session is required to bulk-insert multi-tenanted " +
                $"document type '{typeof(T).FullName}'.");
        }
        if (mapping?.IsMultiTenanted == true && !hasTenantId)
        {
            throw new InvalidOperationException(
                $"Multi-tenanted document type '{typeof(T).FullName}' must expose a writable " +
                "string TenantId property before it can be bulk-inserted.");
        }
        if (string.IsNullOrWhiteSpace(session.TenantId) || !hasTenantId)
            return;

        var metadata = MetadataRegistry.TryGet<T>();
        var tenantProperty = metadata is null
            ? typeof(T).GetProperty("TenantId", typeof(string))
            : null;
        foreach (var entity in entities)
        {
            var entityTenant = metadata is not null
                ? metadata.GetTenantId(entity)
                : tenantProperty?.GetValue(entity) as string;
            if (!string.IsNullOrWhiteSpace(entityTenant)
                && !string.Equals(entityTenant, session.TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Document tenant '{entityTenant}' does not match session tenant " +
                    $"'{session.TenantId}'.");
            }

            if (metadata is not null)
                metadata.SetTenantId(entity, session.TenantId);
            else
                tenantProperty?.SetValue(entity, session.TenantId);
        }
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
