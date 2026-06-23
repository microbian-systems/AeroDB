namespace Dali;

public interface IEvents
{
    /// <summary>
    /// Fetches all events for a stream, wrapped in <see cref="IEvent"/> envelopes
    /// with full metadata (version, sequence, timestamp, stream identity).
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchStream(string streamId, CancellationToken ct = default);

    /// <summary>
    /// Appends events to a stream and returns the wrapped <see cref="IEvent"/> envelopes
    /// with assigned version, sequence, and stream key metadata.
    /// </summary>
    Task<IReadOnlyList<IEvent>> Append(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Starts a new stream by appending the initial events.
    /// Returns the stream ID on success.
    /// </summary>
    Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Fetches all events after a given global sequence number (for async projections / polling).
    /// Uses the global <see cref="IEvent.Sequence"/> (not per-stream version) for ordering
    /// to avoid skipping low-volume streams.
    /// Returns events wrapped in <see cref="IEvent"/> envelopes.
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
        long sequence, CancellationToken ct = default);
}
