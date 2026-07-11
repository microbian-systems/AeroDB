namespace AeroDB.Sable;

/// <summary>
/// Typed append interface for domain event streams.
/// Provides compile-time type safety for appending events to a stream
/// and supports automatic versioning as well as optimistic concurrency.
/// </summary>
public interface IEventStreamAppender
{
    /// <summary>
    /// Appends a single domain event to the stream with automatic versioning.
    /// </summary>
    /// <typeparam name="T">The event type (must be a class).</typeparam>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="event">The event to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of wrapped <see cref="IEvent"/> envelopes with assigned metadata.</returns>
    Task<IReadOnlyList<IEvent>> AppendAsync<T>(string streamId, T @event, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Appends a single domain event to the stream with expected version (optimistic concurrency).
    /// Throws <see cref="ConcurrencyException"/> if the stream's current version does not match.
    /// </summary>
    /// <typeparam name="T">The event type (must be a class).</typeparam>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="expectedVersion">The expected current version of the stream.</param>
    /// <param name="event">The event to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of wrapped <see cref="IEvent"/> envelopes with assigned metadata.</returns>
    Task<IReadOnlyList<IEvent>> AppendAsync<T>(string streamId, long expectedVersion, T @event, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Appends multiple domain events to the stream with automatic versioning.
    /// </summary>
    /// <typeparam name="T">The event type (must be a class).</typeparam>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="events">The events to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of wrapped <see cref="IEvent"/> envelopes with assigned metadata.</returns>
    Task<IReadOnlyList<IEvent>> AppendManyAsync<T>(string streamId, IEnumerable<T> events, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Reads events from the stream as typed envelopes starting from a checkpoint.
    /// Uses global sequence numbers for ordering.
    /// </summary>
    /// <typeparam name="T">The event type to filter (must be a class).</typeparam>
    /// <param name="checkpoint">The global sequence number to start reading from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of typed <see cref="EventEnvelope{T}"/> instances.</returns>
    IAsyncEnumerable<EventEnvelope<T>> ReadFromAsync<T>(long checkpoint, CancellationToken ct = default) where T : class;
}

/// <summary>
/// A typed event envelope with stream context.
/// Wraps a deserialized domain event with its stream identity, version,
/// global sequence number, and timestamp.
/// </summary>
/// <typeparam name="T">The event type.</typeparam>
public sealed class EventEnvelope<T> where T : class
{
    /// <summary>The deserialized domain event.</summary>
    public T Event { get; init; } = default!;

    /// <summary>The stream identifier this event belongs to.</summary>
    public string StreamId { get; init; } = default!;

    /// <summary>The per-stream version number.</summary>
    public long Version { get; init; }

    /// <summary>The global sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>The timestamp when the event was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; }
}
