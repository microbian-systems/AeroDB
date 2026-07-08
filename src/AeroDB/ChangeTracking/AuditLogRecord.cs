using SurrealDb.Net.Models;

namespace AeroDB;

/// <summary>
/// Represents a row in the shared <c>aero_audit_log</c> table.
/// Created automatically by DEFINE EVENT triggers when ChangeTracking() is enabled.
/// </summary>
public class AuditLogRecord : Record
{
    /// <summary>Name of the source table that was changed.</summary>
    public string? SourceTable { get; set; }

    /// <summary>Record ID of the changed document.</summary>
    public RecordId? SourceRecord { get; set; }

    /// <summary>Operation type: "CREATE", "UPDATE", or "DELETE".</summary>
    public string? Operation { get; set; }

    /// <summary>Document state before the change (null on CREATE).</summary>
    public object? BeforeVal { get; set; }

    /// <summary>Document state after the change (null on DELETE).</summary>
    public object? AfterVal { get; set; }

    /// <summary>Timestamp when the change occurred.</summary>
    public DateTime? ChangedAt { get; set; }

    /// <summary>Authenticated user who made the change (from $auth.id).</summary>
    public RecordId? ChangedBy { get; set; }
}
