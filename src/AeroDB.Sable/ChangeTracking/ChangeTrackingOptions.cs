namespace AeroDB.Sable;

/// <summary>
/// Configuration for SurrealDB-native change tracking (audit trail + changefeed).
/// Explicit opt-in per entity via <see cref="DocumentMapping{T}.ChangeTracking"/>.
/// </summary>
public sealed class ChangeTrackingOptions
{
    /// <summary>Enable SurrealDB CHANGEFEED on the document table for SHOW CHANGES replay. Default: true.</summary>
    public bool EnableChangeFeed { get; set; } = true;

    /// <summary>CHANGEFEED retention duration. Default: "30d".</summary>
    public string ChangeFeedRetention { get; set; } = "30d";

    /// <summary>Include original record data in changefeed. Default: true.</summary>
    public bool IncludeOriginal { get; set; } = true;

    /// <summary>Enable audit trail via DEFINE EVENT triggers writing to aero_audit_log. Default: true.</summary>
    public bool EnableAuditTrail { get; set; } = true;

    /// <summary>Enable outbox-style event log (CDC records). Default: false.</summary>
    public bool EnableOutbox { get; set; } = false;

    /// <summary>Track CREATE operations. Default: true.</summary>
    public bool TrackCreates { get; set; } = true;

    /// <summary>Track UPDATE operations. Default: true.</summary>
    public bool TrackUpdates { get; set; } = true;

    /// <summary>Track DELETE operations. Default: true.</summary>
    public bool TrackDeletes { get; set; } = true;

    /// <summary>Use ASYNC event triggers (SurrealDB 3.0+). Default: false for v2.x compat. Sync triggers ensure audit is in-transaction.</summary>
    public bool UseAsyncEvents { get; set; } = false;

    /// <summary>Fields to exclude from before/after snapshots.</summary>
    public IReadOnlyList<string> IgnoredFields { get; init; } = Array.Empty<string>();
}
