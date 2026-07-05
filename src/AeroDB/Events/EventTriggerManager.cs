using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace AeroDB;

public class EventTriggerManager
{
    private readonly ILogger<EventTriggerManager> _logger;

    public EventTriggerManager(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<EventTriggerManager>()
            ?? NullLogger<EventTriggerManager>.Instance;
    }

    public async Task EnsureTriggerAsync(ISurrealDbSession session, EventTriggerDefinition trigger, CancellationToken ct = default)
    {
        var surql = BuildDefineEventSurql(trigger);
        _logger.LogDebug("Ensuring event trigger {Name} on table {Table}", trigger.Name, trigger.Table);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    public async Task RemoveTriggerAsync(ISurrealDbSession session, string name, string table, CancellationToken ct = default)
    {
        var surql = BuildRemoveEventSurql(name, table);
        _logger.LogDebug("Removing event trigger {Name} on table {Table}", name, table);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    public async Task AlterTriggerAsync(ISurrealDbSession session, EventTriggerDefinition trigger, CancellationToken ct = default)
    {
        var surql = BuildDefineEventSurql(trigger).Replace("DEFINE EVENT", "ALTER EVENT", StringComparison.Ordinal);
        _logger.LogDebug("Altering event trigger {Name} on table {Table}", trigger.Name, trigger.Table);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    private static string BuildDefineEventSurql(EventTriggerDefinition trigger)
    {
        var sb = new StringBuilder();
        sb.Append($"DEFINE EVENT {trigger.Name} ON TABLE {trigger.Table}");

        if (trigger.WhenCondition is not null)
            sb.Append($" WHEN {trigger.WhenCondition}");

        if (trigger.Async)
        {
            sb.Append(" ASYNC");
            if (trigger.Retry.HasValue)
                sb.Append($" RETRY {trigger.Retry.Value}");
            if (trigger.MaxDepth.HasValue)
                sb.Append($" MAXDEPTH {trigger.MaxDepth.Value}");
        }

        sb.Append($" THEN ({trigger.Action})");
        return sb.ToString();
    }

    private static string BuildRemoveEventSurql(string name, string table)
    {
        return $"REMOVE EVENT {name} ON TABLE {table};";
    }
}
