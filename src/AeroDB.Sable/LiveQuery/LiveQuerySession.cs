using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace AeroDB.Sable.LiveQuery;

internal sealed class LiveQuerySession : ILiveQuerySession
{
    private readonly ISurrealDbSession _session;
    private readonly StoreOptions _options;
    private readonly string? _tenantId;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _subscriptionLock = new();
    private readonly List<IAsyncDisposable> _subscriptions = new();
    private bool _disposed;

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

    public IAeroDBLiveQueryBuilder<T> Live<T>() where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfEncrypted<T>();
        return new SurrealLiveQueryBuilder<T>(
            _session, _options, _loggerFactory, _tenantId, RegisterSubscription);
    }

    public async Task<IAeroDBLiveQuery<T>> LiveRawQuery<T>(
        string surql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfEncrypted<T>();
        var logger = _loggerFactory.CreateLogger<SurrealAeroDBLiveQuery<T>>();
        var sdkLive = await _session.LiveRawQuery<T>(surql, parameters, ct)
            .ConfigureAwait(false);

        var query = new SurrealAeroDBLiveQuery<T>(
            sdkLive, null, null, null, null,
            _options.LiveQueryChannelCapacity,
            _options.LiveQueryChannelFullMode,
            logger);
        await query.StartAsync(ct).ConfigureAwait(false);
        try
        {
            RegisterSubscription(query);
            return query;
        }
        catch
        {
            await query.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void RegisterSubscription(IAsyncDisposable subscription)
    {
        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] subscriptions;
        lock (_subscriptionLock)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        try
        {
            foreach (var subscription in subscriptions)
                await subscription.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfEncrypted<T>() where T : class
    {
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), _options.Schema))
        {
            throw new SableEncryptedOperationNotSupportedException(
                typeof(T),
                "live query");
        }
    }
}
