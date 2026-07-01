using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace Dali.LiveQuery;

internal sealed class LiveQuerySession : ILiveQuerySession
{
    private readonly ISurrealDbSession _session;
    private readonly StoreOptions _options;
    private readonly string? _tenantId;
    private readonly ILoggerFactory _loggerFactory;

    public LiveQuerySession(
        ISurrealDbSession session,
        StoreOptions options,
        ILoggerFactory? loggerFactory,
        string? tenantId = null)
    {
        _session = session;
        _options = options;
        _tenantId = tenantId;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    }

    public IDaliLiveQueryBuilder<T> Live<T>() where T : class
    {
        return new SurrealLiveQueryBuilder<T>(_session, _options, _loggerFactory, _tenantId);
    }

    public async Task<IDaliLiveQuery<T>> LiveRawQuery<T>(
        string surql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T : class
    {
        var logger = _loggerFactory.CreateLogger<SurrealDaliLiveQuery<T>>();
        var sdkLive = await _session.LiveRawQuery<T>(surql, parameters, ct)
            .ConfigureAwait(false);

        var query = new SurrealDaliLiveQuery<T>(
            sdkLive, null, null, null, null,
            _options.LiveQueryChannelCapacity,
            _options.LiveQueryChannelFullMode,
            logger);
        await query.StartAsync(ct).ConfigureAwait(false);
        return query;
    }

    public ValueTask DisposeAsync()
    {
        // Session-level cleanup — no subscriptions tracked in v1 (M1 backlog)
        return ValueTask.CompletedTask;
    }
}
