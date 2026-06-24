namespace Dali;

/// <summary>The event sourcing API surface. Provides methods for appending events, starting streams, fetching streams, applying optimistic concurrency, archiving streams, and writing tombstone events.</summary>
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
    /// Appends events to a stream with optimistic concurrency. Throws <see cref="ConcurrencyException"/>
    /// if the stream's current version does not match <paramref name="expectedVersion"/>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Append events with optimistic concurrency. The expected version is auto-derived
    /// from the latest version in the stream at the time events were loaded.
    /// Equivalent to <c>Append(streamId, lastKnownVersion, events)</c>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Append events with exclusive locking. Ensures no events exist in the stream yet.
    /// Equivalent to <c>Append(streamId, 0, events)</c>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant of <see cref="AppendOptimistic"/>.</summary>
    Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant of <see cref="AppendExclusive"/>.</summary>
    Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Starts a new stream by appending the initial events.
    /// Returns the stream ID on success.
    /// </summary>
    Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Start a new stream with typed stream identity.</summary>
    Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant with typed stream identity.</summary>
    Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Fetches all events after a given global sequence number (for async projections / polling).
    /// Uses the global <see cref="IEvent.Sequence"/> (not per-stream version) for ordering
    /// to avoid skipping low-volume streams.
    /// Returns events wrapped in <see cref="IEvent"/> envelopes.
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
        long sequence, CancellationToken ct = default);

    /// <summary>
    /// Archive a stream — moves events to the archive table.
    /// After archiving, the stream cannot receive new events.
    /// </summary>
    Task ArchiveStream(string streamId, CancellationToken ct = default);

    /// <summary>Guid variant.</summary>
    Task ArchiveStream(Guid streamId, CancellationToken ct = default);

    /// <summary>
    /// Write a tombstone event to fill a gap caused by a failed transaction.
    /// Use this for idempotent retry scenarios.
    /// </summary>
    Task<IReadOnlyList<IEvent>> WriteTombstone(string streamId, long version, CancellationToken ct = default);
}
