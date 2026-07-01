using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Extension methods for registering self-aggregating snapshot projections.
/// </summary>
public static class SnapshotExtensions
{
    /// <summary>
    /// Register a self-aggregating snapshot projection for <typeparamref name="T"/>.
    /// The aggregate type applies events to itself via <c>Apply(EventType)</c> methods.
    /// </summary>
    /// <param name="projections">The projections list (typically <c>store.Options.Projections</c>).</param>
    /// <param name="configure">Optional configuration delegate.</param>
    [Obsolete("Use opts.Projections.Snapshot<T>(lifecycle) for Marten 1:1 parity.")]
    public static void Snapshot<T>(this List<IProjection> projections, Action<SnapshotOptions>? configure = null)
        where T : Record, new()
    {
        var options = new SnapshotOptions();
        configure?.Invoke(options);
        projections.Add(new SnapshotProjection<T>(options));
    }
}
