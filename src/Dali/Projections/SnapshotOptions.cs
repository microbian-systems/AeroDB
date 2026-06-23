namespace Dali;

/// <summary>
/// Configuration for <see cref="SnapshotProjection{T}"/>.
/// </summary>
public class SnapshotOptions
{
    /// <summary>Maximum events to keep in a stream before taking a snapshot. Default: 50.</summary>
    public int SnapshotFrequency { get; set; } = 50;
    
    /// <summary>Projection lifecycle. Default: Inline for consistency.</summary>
    public ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Inline;
}
