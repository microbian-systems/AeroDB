namespace AeroDB;

/// <summary>When a snapshot projection should be rebuilt/refreshed. Maps to Marten's SnapshotLifecycle.</summary>
public enum SnapshotLifecycle
{
    /// <summary>Snapshot is built inline with each event append.</summary>
    Inline,
    /// <summary>Snapshot is built asynchronously by the daemon.</summary>
    Async
}
