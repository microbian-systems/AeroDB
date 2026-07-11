namespace AeroDB.Sable;

/// <summary>
/// Tracks the last processed event sequence number for the async daemon,
/// persisted in the mt_projection_progress table to prevent full event replay on restart.
/// </summary>
public class ProjectionProgress
{
    /// <summary>
    /// Name of the projection (e.g., "async_daemon").
    /// </summary>
    public string ProjectionName { get; set; } = "";

    /// <summary>
    /// The global event sequence number that was last processed.
    /// </summary>
    public long LastVersion { get; set; }

    /// <summary>
    /// When this progress record was last updated.
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }
}
