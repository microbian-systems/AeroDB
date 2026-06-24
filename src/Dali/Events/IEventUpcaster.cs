namespace Dali;

/// <summary>
/// Transforms old event versions to new event types during deserialization.
/// Register upcasters via <see cref="EventSourcingOptions.Upcasters"/>.
/// </summary>
public interface IEventUpcaster
{
    /// <summary>The old event type name this upcaster handles.</summary>
    string OldEventType { get; }

    /// <summary>
    /// Transform old event data into the new event type.
    /// </summary>
    object Upcast(object oldEvent);
}
