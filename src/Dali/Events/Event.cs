namespace Dali;

/// <summary>
/// Default implementation of <see cref="IEvent{T}"/> and <see cref="IEvent"/>.
/// Immutable record — create via <see cref="EventStore"/>, never mutate.
/// </summary>
public sealed record Event<T>(
    T Data,
    long Version,
    long Sequence,
    DateTimeOffset Timestamp,
    string StreamId,
    Guid StreamKey,
    Dictionary<string, string>? Headers = null
) : IEvent<T>, IEvent
{
    /// <summary>
    /// Explicit non-generic interface implementation. Returns <see cref="Data"/> as <c>object</c>.
    /// Boxing may occur for value-type <typeparamref name="T"/> — access via <see cref="IEvent{T}"/>
    /// to avoid allocation.
    /// </summary>
    object IEvent.Data => Data!;
}
