using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Creates/updates one projected document per event stream.
/// The document identity is derived from the stream ID stored on the first event.
/// </summary>
/// <typeparam name="T">The projected document type (must extend <see cref="Record"/>).</typeparam>
public abstract class SingleStreamProjection<T> : InlineProjection<T> where T : Record
{
    /// <summary>
    /// Derives the projected document identity from the stream ID of the first event.
    /// Events are expected to have a <c>StreamId</c> property (string).
    /// </summary>
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Cannot derive document ID from empty events.");

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
