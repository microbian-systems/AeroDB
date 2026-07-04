namespace Dali;

/// <summary>
/// A projection that composites multiple sub-projections into one logical projection.
/// Events are dispatched to all sub-projections; each sub-projection maintains
/// its own document table. Equivalent to Marten's composite projections.
/// </summary>
public class CompositeProjection : IProjection
{
    private readonly string _name;
    private readonly List<IProjection> _subProjections = new();
    private ProjectionLifecycle _lifecycle = ProjectionLifecycle.Async;

    public CompositeProjection(string name)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <inheritdoc />
    public Type[] EventTypes => _subProjections.SelectMany(p => p.EventTypes).Distinct().ToArray();

    /// <inheritdoc />
    public ProjectionLifecycle Lifecycle => _lifecycle;

    /// <summary>Set the projection lifecycle. Default is Async.</summary>
    public CompositeProjection Life(ProjectionLifecycle lifecycle)
    {
        _lifecycle = lifecycle;
        return this;
    }

    /// <summary>Add a sub-projection to this composite.</summary>
    public CompositeProjection Add<T>(T projection) where T : IProjection
    {
        _subProjections.Add(projection);
        return this;
    }

    /// <summary>The registered sub-projections.</summary>
    public IReadOnlyList<IProjection> SubProjections => _subProjections.AsReadOnly();

    /// <inheritdoc />
    public async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        // Dispatch events to all sub-projections in parallel
        var tasks = _subProjections.Select(p => p.ApplyAsync(context, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        foreach (var proj in _subProjections)
            await proj.RebuildAsync(session, ct).ConfigureAwait(false);
    }
}
