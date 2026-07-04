namespace AeroDB;

/// <summary>
/// Typed event envelope carrying metadata (version, timestamp, sequence, stream identity)
/// alongside the domain event data. Replaces bare <c>object</c> events throughout the pipeline.
/// </summary>
public interface IEvent<out T>
{
    T Data { get; }
    long Version { get; }
    long Sequence { get; }
    DateTimeOffset Timestamp { get; }
    string StreamId { get; }
    Guid StreamKey { get; }

    /// <summary>Opaque headers dictionary for event metadata (causation, correlation, etc.).</summary>
    Dictionary<string, string>? Headers { get; }
}

/// <summary>
/// Non-generic event envelope. <c>Event&lt;T&gt;</c> implements both <see cref="IEvent{T}"/>
/// and <see cref="IEvent"/> for maximum compatibility.
/// </summary>
/// <remarks>
/// This is deliberately <b>not</b> <c>IEvent&lt;object&gt;</c> because C# does not allow
/// implicit conversion from <c>Event&lt;T&gt;</c> (which implements <c>IEvent&lt;T&gt;</c>)
/// to a separate interface that inherits from <c>IEvent&lt;object&gt;</c>.
/// Instead, <see cref="Event{T}"/> explicitly implements both interfaces.
/// </remarks>
public interface IEvent
{
    object Data { get; }
    long Version { get; }
    long Sequence { get; }
    DateTimeOffset Timestamp { get; }
    string StreamId { get; }
    Guid StreamKey { get; }

    /// <summary>Opaque headers dictionary for event metadata (causation, correlation, etc.).</summary>
    Dictionary<string, string>? Headers { get; }
}
