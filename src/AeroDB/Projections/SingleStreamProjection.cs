namespace AeroDB;

/// <summary>
/// Creates/updates one projected document per event stream.
/// The document identity is derived from the stream ID stored on the first event.
/// </summary>
/// <typeparam name="T">The projected document type.</typeparam>
public abstract class SingleStreamProjection<T> : InlineProjection<T> where T : class
{
    /// <summary>
    /// Derives the projected document identity from the stream ID of the first event.
    /// Uses <see cref="IEvent.StreamId"/> directly when available (Phase 1+),
    /// otherwise falls back to reflection on a <c>StreamId</c> property (legacy).
    /// </summary>
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Cannot derive document ID from empty events.");

        // Try IEvent first (Phase 1+)
        if (events[0] is IEvent ievt)
            return ievt.StreamId ?? throw new InvalidOperationException("StreamId is null on the first event.");

        // Fallback: reflection on event StreamId property (legacy)
        var evt = events[0];
        var streamProp = evt.GetType().GetProperty("StreamId");
        if (streamProp is null)
            throw new InvalidOperationException(
                $"Event type {evt.GetType().Name} does not have a StreamId property. " +
                "SingleStreamProjection requires StreamId on events.");

        return streamProp.GetValue(evt)?.ToString()
            ?? throw new InvalidOperationException("StreamId is null on the first event.");
    }
}
