namespace AeroDB;

/// <summary>Configuration options for creating a document session.</summary>
public class SessionOptions
{
    /// <summary>Document tracking mode. Defaults to <see cref="DocumentTracking.None"/>.</summary>
    public DocumentTracking Tracking { get; set; } = DocumentTracking.None;

    /// <summary>Maximum execution time for this session's database operations.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Session-scoped listeners applied only to this session.</summary>
    public List<IDocumentSessionListener> Listeners { get; set; } = new();

    /// <summary>Overrides the tenant ID for this session.</summary>
    public string? TenantId { get; set; }

    /// <summary>Per-session document policies applied on top of store-level policies.</summary>
    public List<IDocumentPolicy> Policies { get; set; } = new();

    /// <summary>Per-session change subscribers notified on SaveChangesAsync.</summary>
    public List<object> Subscribers { get; set; } = new();
}
