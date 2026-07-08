using System.Text;

namespace AeroDB;

/// <summary>
/// Generates SurrealDB DDL for change tracking: CHANGEFEED clause and DEFINE EVENT trigger definitions.
/// Uses <c>aero_audit_</c> prefix for trigger names to avoid collision with user-defined triggers.
/// </summary>
internal static class ChangeTrackingGenerator
{
    /// <summary>
    /// Builds the CHANGEFEED clause for a DEFINE TABLE or ALTER TABLE statement.
    /// Example: "CHANGEFEED 30d INCLUDE ORIGINAL"
    /// Returns null if ChangeFeed is disabled.
    /// </summary>
    public static string? BuildChangeFeedClause(ChangeTrackingOptions options)
    {
        if (!options.EnableChangeFeed)
            return null;

        var sb = new StringBuilder();
        sb.Append($"CHANGEFEED {options.ChangeFeedRetention}");
        if (options.IncludeOriginal)
            sb.Append(" INCLUDE ORIGINAL");
        return sb.ToString();
    }

    /// <summary>
    /// Generates an ALTER TABLE statement to add/update the CHANGEFEED clause.
    /// </summary>
    public static string BuildAlterChangeFeedSurql(string tableName, ChangeTrackingOptions options)
    {
        var clause = BuildChangeFeedClause(options);
        if (clause is null)
            return "";
        return $"ALTER TABLE {tableName} {clause};";
    }

    /// <summary>
    /// Generates DEFINE EVENT trigger definitions for a tracked table.
    /// Uses <c>aero_audit_</c> prefix: <c>aero_audit_created_{table}</c>, <c>aero_audit_updated_{table}</c>, <c>aero_audit_deleted_{table}</c>.
    /// </summary>
    public static List<EventTriggerDefinition> GenerateTriggers(
        string tableName,
        ChangeTrackingOptions options)
    {
        var triggers = new List<EventTriggerDefinition>();

        if (options.TrackCreates)
        {
            triggers.Add(new EventTriggerDefinition
            {
                Name = $"aero_audit_created_{tableName}",
                Table = tableName,
                WhenCondition = "$event = \"CREATE\"",
                Async = options.UseAsyncEvents,
                Action = $@"
    CREATE aero_audit_log SET
        source_table = ""{tableName}"",
        source_record = $value.id,
        operation = $event,
        before_val = NONE,
        after_val = $value,
        changed_at = time::now(),
        changed_by = $auth.id
"
            });
        }

        if (options.TrackUpdates)
        {
            triggers.Add(new EventTriggerDefinition
            {
                Name = $"aero_audit_updated_{tableName}",
                Table = tableName,
                WhenCondition = "$event = \"UPDATE\"",
                Async = options.UseAsyncEvents,
                Action = $@"
    CREATE aero_audit_log SET
        source_table = ""{tableName}"",
        source_record = $value.id,
        operation = $event,
        before_val = $before,
        after_val = $after,
        changed_at = time::now(),
        changed_by = $auth.id
"
            });
        }

        if (options.TrackDeletes)
        {
            triggers.Add(new EventTriggerDefinition
            {
                Name = $"aero_audit_deleted_{tableName}",
                Table = tableName,
                WhenCondition = "$event = \"DELETE\"",
                Async = options.UseAsyncEvents,
                Action = $@"
    CREATE aero_audit_log SET
        source_table = ""{tableName}"",
        source_record = $value.id,
        operation = $event,
        before_val = $before,
        after_val = NONE,
        changed_at = time::now(),
        changed_by = $auth.id
"
            });
        }

        return triggers;
    }

    /// <summary>
    /// Builds the SurrealQL for creating the shared aero_audit_log table.
    /// Call once per database initialization.
    /// </summary>
    public static string BuildAuditLogTableSurql()
    {
        return @"
DEFINE TABLE aero_audit_log SCHEMAFULL;

DEFINE FIELD source_table ON TABLE aero_audit_log TYPE string;
DEFINE FIELD source_record ON TABLE aero_audit_log TYPE record;
DEFINE FIELD operation ON TABLE aero_audit_log TYPE string;
DEFINE FIELD before_val ON TABLE aero_audit_log TYPE option<object>;
DEFINE FIELD after_val ON TABLE aero_audit_log TYPE option<object>;
DEFINE FIELD changed_at ON TABLE aero_audit_log TYPE datetime DEFAULT time::now();
DEFINE FIELD changed_by ON TABLE aero_audit_log TYPE option<record>;
";
    }
}
