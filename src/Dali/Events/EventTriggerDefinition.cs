namespace Dali;

public sealed class EventTriggerDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string? WhenCondition { get; set; }
    public string Action { get; set; } = string.Empty;
    public bool Async { get; set; }
    public int? Retry { get; set; }
    public int? MaxDepth { get; set; }
}

public class EventTriggerOptions
{
    public List<EventTriggerDefinition> Triggers { get; } = new();
    public bool AutoCreateTriggers { get; set; } = true;

    public EventTriggerOptions AddTrigger(string name, string table, string action,
        string? whenCondition = null, bool async = false, int? retry = null, int? maxDepth = null)
    {
        Triggers.Add(new EventTriggerDefinition
        {
            Name = name,
            Table = table,
            Action = action,
            WhenCondition = whenCondition,
            Async = async,
            Retry = retry,
            MaxDepth = maxDepth
        });
        return this;
    }
}
