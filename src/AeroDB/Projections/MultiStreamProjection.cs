using SurrealDb.Net.Models;

namespace AeroDB;

/// <summary>
/// Creates/updates projected documents that span multiple event streams.
/// The document identity is defined by the subclass (e.g., from a correlation ID
/// shared across streams).
/// </summary>
/// <typeparam name="T">The projected document type (must extend <see cref="Record"/>).</typeparam>
public abstract class MultiStreamProjection<T> : InlineProjection<T> where T : Record
{
    private Func<IQuerySession, IReadOnlyList<IEvent>, CancellationToken, Task<IEventGrouping<object>>>? _customGrouper;

    /// <summary>
    /// Determines the projected document identity from the events.
    /// This can be a string (record key) or any identifier.
    /// </summary>
    protected abstract override object GetDocumentId(IReadOnlyList<object> events);

    /// <summary>
    /// Register a custom event grouper for this multi-stream projection.
    /// The grouper determines which aggregate each event belongs to.
    /// </summary>
    public void CustomGrouping<TId>(IAggregateGrouper<TId> grouper) where TId : notnull
    {
        _customGrouper = async (session, events, ct) =>
        {
            var grouping = await grouper.GroupAsync(session, events, ct).ConfigureAwait(false);
            // Reconstruct as object-keyed grouping — IEventGrouping<TId> is invariant
            var result = new EventGrouping<object>();
            foreach (var (id, groupEvents) in grouping.Groups)
                result.Add(id!, groupEvents);
            return result;
        };
    }

    /// <summary>
    /// Register a custom event grouping function.
    /// </summary>
    public void CustomGrouping(Func<IQuerySession, IReadOnlyList<IEvent>, CancellationToken, Task<IEventGrouping<object>>> grouper)
    {
        _customGrouper = grouper;
    }

    /// <summary>
    /// Whether a custom grouper has been registered.
    /// </summary>
    public bool HasCustomGrouper => _customGrouper is not null;

    /// <summary>
    /// Invoke the custom grouper, if registered, to group events by aggregate ID.
    /// </summary>
    internal async Task<IEventGrouping<object>?> GroupEventsAsync(
        IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken ct)
    {
        if (_customGrouper is not null)
            return await _customGrouper(session, events, ct).ConfigureAwait(false);
        return null;
    }
}
