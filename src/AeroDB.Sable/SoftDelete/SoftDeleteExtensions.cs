using AeroDB.Sable.Metadata;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

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
        var options = internalSession.StoreOptions;
        var mapping = schema.Mappings.GetValueOrDefault(typeof(T));
        if (options.TenancyStyle != TenancyStyle.DatabasePerTenant
            && mapping?.IsMultiTenanted == true
            && string.IsNullOrWhiteSpace(internalSession.TenantId))
        {
            throw new InvalidOperationException(
                $"A tenant-scoped session is required to soft-delete multi-tenanted " +
                $"document type '{typeof(T).FullName}'.");
        }
        if (options.TenancyStyle != TenancyStyle.DatabasePerTenant
            && mapping?.IsMultiTenanted == true
            && !MetadataDispatch.HasTenantId(typeof(T)))
        {
            throw new InvalidOperationException(
                $"Multi-tenanted document type '{typeof(T).FullName}' must expose a writable " +
                "string TenantId property before it can be soft-deleted.");
        }

        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(typeof(T), schema);
        var deletedField = MetadataDispatch.GetFieldName(typeof(T), nameof(ISoftDeleted.Deleted), schema);
        var deletedAtField = MetadataDispatch.GetFieldName(typeof(T), nameof(ISoftDeleted.DeletedAt), schema);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__sable_soft_delete_record"] = RecordId.From(table, recordId),
            ["__sable_soft_delete_at"] = DateTimeOffset.UtcNow
        };
        var whereClause = "";
        if (options.TenancyStyle != TenancyStyle.DatabasePerTenant
            && !string.IsNullOrWhiteSpace(internalSession.TenantId)
            && MetadataDispatch.HasTenantId(typeof(T)))
        {
            var tenantField = MetadataDispatch.GetFieldName(typeof(T), "TenantId", schema);
            parameters["__sable_soft_delete_tenant"] = internalSession.TenantId;
            whereClause = $" WHERE {tenantField} = $__sable_soft_delete_tenant";
        }

        var surql = $"UPDATE $__sable_soft_delete_record " +
            $"SET {deletedField} = true, {deletedAtField} = $__sable_soft_delete_at" +
            $"{whereClause};";

        var targetSession = await internalSession
            .GetSessionForSchemaAsync(mappedDatabase, ct)
            .ConfigureAwait(false);
        var response = await targetSession.RawQuery(surql, parameters, ct).ConfigureAwait(false);
        response.EnsureAllOks();
    }
}
