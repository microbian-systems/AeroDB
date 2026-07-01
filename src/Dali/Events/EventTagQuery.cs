namespace Dali;

/// <summary>
/// A query to check if events exist with specific tags.
/// Used by the async daemon to determine if there are events to project.
/// </summary>
public class EventTagQuery
{
    /// <summary>The tag values to query for.</summary>
    public IReadOnlyList<string> TagValues { get; }

    /// <summary>Optional event type filter.</summary>
    public string? EventType { get; }

    public EventTagQuery(IReadOnlyList<string> tagValues, string? eventType = null)
    {
        TagValues = tagValues ?? throw new ArgumentNullException(nameof(tagValues));
        EventType = eventType;
    }
}
