namespace AeroDB.Sable;

/// <summary>
/// A projection shard — one worker that processes events for a specific projection.
/// Each shard has independent progress tracking, health monitoring, and lifecycle.
/// </summary>
public sealed class ProjectionShard
{
    /// <summary>The shard name. Defaults to projection type name.</summary>
    public string Name { get; }

    /// <summary>The projection this shard processes.</summary>
    public IProjection Projection { get; }

    /// <summary>Current health state. Updated on each poll cycle.</summary>
    public DaemonHealthState Health { get; set; }

    /// <summary>Current sequence watermark for this shard.</summary>
    public long Watermark { get; set; }

    /// <summary>Whether the shard is active.</summary>
    public bool IsActive { get; set; }

    /// <summary>Aggregate cache scoped to this shard.</summary>
    public AggregateCache Cache { get; } = new(1000);

    internal ProjectionShard(string name, IProjection projection)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        Health = new DaemonHealthState(false, null, null, 0, 0, null);
    }
}
