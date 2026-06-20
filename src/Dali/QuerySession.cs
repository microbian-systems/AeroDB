using Dali.Metadata;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace Dali;

public class QuerySession : InternalSessionBase, IQuerySession
{
    private readonly ILogger<QuerySession> _logger;

    public QuerySession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options)
        : base(client, session, options)
    {
        _logger = CreateLogger<QuerySession>();
    }

    /// <summary>Starts a live query monitoring a table for changes.</summary>
    public async Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var live = await Session.LiveTable<T>(table, diff: false, ct).ConfigureAwait(false);
        return new LiveQuery<T>(live);
    }

    /// <summary>Starts a live query with a custom where clause.</summary>
    public async Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"LIVE SELECT * FROM `{table}` WHERE {whereClause}";
        var live = await Session.LiveRawQuery<T>(surql, null, ct).ConfigureAwait(false);
        return new LiveQuery<T>(live);
    }

    /// <summary>Watches events for a specific stream ID.</summary>
    public async Task<ILiveQuery<object>> WatchStreamAsync(string streamId, CancellationToken ct = default)
    {
        var surql = $"LIVE SELECT * FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}'";
        var live = await Session.LiveRawQuery<object>(surql, null, ct).ConfigureAwait(false);
        return new LiveQuery<object>(live);
    }
}
