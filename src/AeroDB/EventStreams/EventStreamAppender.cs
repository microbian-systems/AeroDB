using System.Runtime.CompilerServices;

namespace AeroDB;

/// <summary>
/// Typed event stream appender wrapping the existing <see cref="IEvents"/> infrastructure.
/// Provides compile-time type safety for appending and reading domain events.
/// </summary>
internal sealed class EventStreamAppender : IEventStreamAppender
{
    private readonly IEvents _events;
    private readonly IDocumentSession _session;

    /// <summary>
    /// Initializes a new instance of <see cref="EventStreamAppender"/>.
    /// </summary>
    /// <param name="session">The document session providing access to the event store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is null.</exception>
    public EventStreamAppender(IDocumentSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _events = session.Events;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IEvent>> AppendAsync<T>(string streamId, T @event, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(@event);

        return await _events.Append(streamId, new object[] { @event }, ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IEvent>> AppendAsync<T>(string streamId, long expectedVersion, T @event, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(@event);

        return await _events.Append(streamId, expectedVersion, new object[] { @event }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IEvent>> AppendManyAsync<T>(string streamId, IEnumerable<T> events, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(events);

        var eventList = events.Cast<object>().ToList();
        return await _events.Append(streamId, eventList, ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope<T>> ReadFromAsync<T>(long checkpoint, [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        // Read from the event stream using the existing FetchAllAfterSequence
        var rawEvents = await _events.FetchAllAfterSequence(checkpoint, ct).ConfigureAwait(false);

        foreach (var e in rawEvents)
        {
            if (e.Data is T typedEvent)
            {
                yield return new EventEnvelope<T>
                {
                    Event = typedEvent,
                    StreamId = e.StreamId.Value,
                    Version = e.Version,
                    Sequence = e.Sequence,
                    Timestamp = e.Timestamp
                };
            }
        }
    }
}
