using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace AeroDB.Sable;

/// <summary>
/// Manages schema changes for change tracking: creates the shared aero_audit_log table,
/// applies CHANGEFEED to tracked tables, and ensures DEFINE EVENT triggers.
/// </summary>
internal sealed class ChangeTrackingSchemaManager
{
    private readonly ILogger<ChangeTrackingSchemaManager> _logger;

    public ChangeTrackingSchemaManager(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ChangeTrackingSchemaManager>()
            ?? NullLogger<ChangeTrackingSchemaManager>.Instance;
    }

    /// <summary>
    /// Applies change tracking schema for all tracked document mappings in the store.
    /// Called during <see cref="DocumentStore.InitializeAsync"/>.
    /// </summary>
    public async Task ApplyAsync(StoreOptions options, ISurrealDbSession session, CancellationToken ct = default)
    {
        // Find all mappings with ChangeTracking enabled
        var trackedMappings = options.Schema.Mappings.Values
            .Where(m => m.ChangeTrackingConfig is not null)
            .ToList();

        if (trackedMappings.Count == 0)
            return;

        _logger.LogInformation("Applying change tracking for {Count} document types", trackedMappings.Count);

        // Step 1: Create shared audit log table (once per database)
        var auditLogSurql = ChangeTrackingGenerator.BuildAuditLogTableSurql();
        await session.RawQuery(auditLogSurql, null, ct).ConfigureAwait(false);

        var triggerManager = new EventTriggerManager();

        foreach (var mapping in trackedMappings)
        {
            var tableName = Metadata.MetadataDispatch.GetTableName(mapping.EntityType, options.Schema);
            var config = mapping.ChangeTrackingConfig!;

            // Step 2: Apply CHANGEFEED to the table
            var alterSurql = ChangeTrackingGenerator.BuildAlterChangeFeedSurql(tableName, config);
            if (!string.IsNullOrEmpty(alterSurql))
            {
                _logger.LogDebug("Applying CHANGEFEED to table {Table}", tableName);
                await session.RawQuery(alterSurql, null, ct).ConfigureAwait(false);
            }

            // Step 3: Generate and apply DEFINE EVENT triggers
            if (config.EnableAuditTrail)
            {
                var triggers = ChangeTrackingGenerator.GenerateTriggers(tableName, config);
                foreach (var trigger in triggers)
                {
                    _logger.LogDebug("Ensuring audit trigger {Name} on table {Table}", trigger.Name, trigger.Table);
                    await triggerManager.EnsureTriggerAsync(session, trigger, ct).ConfigureAwait(false);
                }
            }
        }

        _logger.LogInformation("Change tracking applied for {Count} document types", trackedMappings.Count);
    }
}
