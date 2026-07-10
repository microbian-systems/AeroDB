namespace AeroDB.Sable;

/// <summary>
/// A slice of events for a specific stream, with optional pre-loaded aggregate.
/// Equivalent to Marten's <c>IEventSlice&lt;T&gt;</c>.
/// </summary>
public interface IEventSlice<T>
{
    /// <summary>The stream/aggregate identifier.</summary>
    string Id { get; }

    /// <summary>The events in this slice.</summary>
    IReadOnlyList<IEvent> Events { get; }

    /// <summary>
    /// The pre-loaded aggregate document, or null if not yet fetched.
    /// Inline projections use this as the starting state.
    /// </summary>
    T? Aggregate { get; }
}

/// <summary>Concrete implementation of <see cref="IEventSlice{T}"/>.</summary>
public sealed class EventSlice<T> : IEventSlice<T>
{
    /// <inheritdoc />
    public string Id { get; init; } = string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<IEvent> Events { get; init; } = Array.Empty<IEvent>();

    /// <inheritdoc />
    public T? Aggregate { get; init; }
}
