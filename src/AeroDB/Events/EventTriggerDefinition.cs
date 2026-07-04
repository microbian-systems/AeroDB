using AeroDB.Metadata;

namespace AeroDB;

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

    public EventTriggerOptions AddTrigger<T>(string name, string action,
        string? whenCondition = null, bool async = false, int? retry = null, int? maxDepth = null)
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
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

    /// <summary>
    /// Adds a trigger with the action built from a <see cref="TriggerActionBuilder"/>.
    /// Provides compile-time-safe field references for CREATE/UPDATE/DELETE/INSERT/RELATE statements.
    /// </summary>
    public EventTriggerOptions AddTrigger<T>(string name,
        Func<TriggerActionBuilder, TriggerActionBuilder> actionBuilder,
        string? whenCondition = null, bool async = false, int? retry = null, int? maxDepth = null)
    {
        var builder = new TriggerActionBuilder();
        actionBuilder(builder);
        var action = builder.Build();
        return AddTrigger<T>(name, action, whenCondition, async, retry, maxDepth);
    }
}
