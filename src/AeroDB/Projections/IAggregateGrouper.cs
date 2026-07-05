namespace AeroDB;

/// <summary>
/// Represents a grouping of events by aggregate identifier.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public interface IEventGrouping<TId> where TId : notnull
{
    /// <summary>
    /// All groups with their events, keyed by aggregate ID.
    /// </summary>
    IReadOnlyDictionary<TId, IReadOnlyList<IEvent>> Groups { get; }
}

/// <summary>
/// Groups events into aggregate groups for multi-stream projections.
/// Each group produces one projected document.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public interface IAggregateGrouper<TId> where TId : notnull
{
    /// <summary>
    /// Marten-compatible grouping hook. Implementers may add events to the supplied grouping.
    /// </summary>
    Task Group(IQuerySession session, IReadOnlyList<IEvent> events, ITenantSliceGroup<TId> grouping)
        => Task.CompletedTask;

    /// <summary>
    /// Given a slice of events, produce event groups keyed by aggregate ID.
    /// Each group maps to one projected document.
    /// </summary>
    Task<IEventGrouping<TId>> GroupAsync(
        IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken ct)
    {
        var grouping = new EventGrouping<TId>();
        return Group(session, events, grouping).ContinueWith(
            _ => (IEventGrouping<TId>)grouping,
            ct,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>
/// Marten-compatible mutable grouping abstraction for multi-stream projections.
/// </summary>
public interface ITenantSliceGroup<TId> where TId : notnull
{
    void AddEvents(TId id, IReadOnlyList<IEvent> events);

    void AddEvents(TId id, IEnumerable<IEvent> events)
        => AddEvents(id, events.ToList());
}

/// <summary>
/// Default implementation of <see cref="IEventGrouping{TId}"/>.
/// Maps aggregate IDs to their associated events.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public sealed class EventGrouping<TId> : IEventGrouping<TId>, ITenantSliceGroup<TId> where TId : notnull
{
    private readonly Dictionary<TId, List<IEvent>> _groups = new();

    /// <summary>Add events to a group identified by aggregate ID.</summary>
    public void Add(TId id, IReadOnlyList<IEvent> events)
    {
        if (!_groups.TryGetValue(id, out var list))
            _groups[id] = list = new List<IEvent>();
        list.AddRange(events);
    }

    public void AddEvents(TId id, IReadOnlyList<IEvent> events) => Add(id, events);

    public void AddEvents(TId id, IEnumerable<IEvent> events) => Add(id, events.ToList());

    /// <summary>
    /// All groups with their events, keyed by aggregate ID.
    /// </summary>
    public IReadOnlyDictionary<TId, IReadOnlyList<IEvent>> Groups =>
        _groups.ToDictionary(k => k.Key, v => (IReadOnlyList<IEvent>)v.Value.AsReadOnly());
}
