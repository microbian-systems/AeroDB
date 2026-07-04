using System.Collections;

namespace AeroDB;

/// <summary>
/// Fluent projection registration API. Replaces the raw <see cref="List{IProjection}"/>
/// on <see cref="StoreOptions.Projections"/>. Implements <see cref="IList{IProjection}"/>
/// for backward compatibility.
/// </summary>
public class ProjectionCollection : IList<IProjection>, IReadOnlyList<IProjection>
{
    private readonly List<IProjection> _projections = new();

    // --- IList<IProjection> delegation ---
    public int Count => _projections.Count;
    public bool IsReadOnly => false;
    public IProjection this[int index] { get => _projections[index]; set => _projections[index] = value; }
    public IEnumerator<IProjection> GetEnumerator() => _projections.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(IProjection item) => _projections.Add(item);
    public void Clear() => _projections.Clear();
    public bool Contains(IProjection item) => _projections.Contains(item);
    public void CopyTo(IProjection[] array, int arrayIndex) => _projections.CopyTo(array, arrayIndex);
    public bool Remove(IProjection item) => _projections.Remove(item);
    public int IndexOf(IProjection item) => _projections.IndexOf(item);
    public void Insert(int index, IProjection item) => _projections.Insert(index, item);
    public void RemoveAt(int index) => _projections.RemoveAt(index);

    /// <summary>Internal access to the raw list for enumeration.</summary>
    internal List<IProjection> InnerList => _projections;

    // === GAP: Add<T>(ProjectionLifecycle) ===

    /// <summary>
    /// Register a projection type with the given lifecycle. Instantiates
    /// <typeparamref name="T"/> via its parameterless constructor and sets
    /// <see cref="IProjection.Lifecycle"/>.
    /// </summary>
    public void Add<T>(ProjectionLifecycle lifecycle) where T : IProjection, new()
    {
        var projection = new T();
        projection.Lifecycle = lifecycle;
        Add(projection);
    }

    /// <summary>
    /// Register an existing projection instance with the given lifecycle.
    /// </summary>
    public void Add(IProjection projection, ProjectionLifecycle lifecycle)
    {
        projection.Lifecycle = lifecycle;
        Add(projection);
    }

    // === GAP: Snapshot<T>(SnapshotLifecycle) ===

    /// <summary>
    /// Register a self-aggregating snapshot projection. The aggregate type
    /// <typeparamref name="T"/> applies events to itself via <c>Apply(EventType)</c>
    /// or <c>When(EventType)</c> methods.
    /// </summary>
    public void Snapshot<T>(SnapshotLifecycle lifecycle, Action<SnapshotOptions>? configure = null)
        where T : class, new()
    {
        var options = new SnapshotOptions();
        configure?.Invoke(options);
        // Map SnapshotLifecycle to ProjectionLifecycle; explicit parameter takes precedence
        options.Lifecycle = lifecycle == SnapshotLifecycle.Inline
            ? ProjectionLifecycle.Inline
            : ProjectionLifecycle.Async;
        var projection = new SnapshotProjection<T>(options);
        Add(projection);
    }

    // === GAP: LiveStreamAggregation<T>() ===

    /// <summary>
    /// Register a live (on-the-fly) stream aggregation. No projection data is stored;
    /// results are computed at read time by replaying stream events.
    /// </summary>
    public void LiveStreamAggregation<T>() where T : class, new()
    {
        // Live aggregation doesn't need a stored projection — the daemon skips it.
        // Create a marker projection that the daemon ignores.
        var projection = new LiveStreamAggregationProjection<T>();
        Add(projection);
    }
}

/// <summary>
/// Internal marker projection for live stream aggregation. Carries no stored
/// projection data — the daemon skips it, and reads are computed on-the-fly
/// via <see cref="LiveStreamAggregation"/>.
/// </summary>
internal class LiveStreamAggregationProjection<T> : IProjection where T : class
{
    public Type[] EventTypes => Array.Empty<Type>();
    public ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Live;
    public string Name => $"Live:{typeof(T).Name}";

    public Task ApplyAsync(IProjectionContext context, CancellationToken ct)
        => Task.CompletedTask;

    public Task RebuildAsync(IDocumentSession session, CancellationToken ct)
        => Task.CompletedTask;
}
