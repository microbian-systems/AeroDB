using System.Reflection;
using AeroDB.Sable.LiveQuery;
using AeroDB.Sable.Metadata;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace AeroDB.Sable;

/// <summary>The concrete query session implementing <see cref="IQuerySession"/>. Provides read-only access to SurrealDB via LINQ, raw SQL, compiled queries, live notifications, and graph traversal.</summary>
public class QuerySession : InternalSessionBase, IQuerySession
{
    private readonly ILogger<QuerySession> _logger;

    public QuerySession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, DocumentTracking tracking)
        : base(client, session, options, tracking)
    {
        _logger = CreateLogger<QuerySession>();
    }

    // ===================================================================
    // Marten API parity: Watch* live query wrappers
    // ===================================================================

    /// <summary>
    /// [Marten API parity] Starts a live query monitoring a table.
    /// Delegates to <see cref="ILiveQuerySession.Live{T}"/> internally,
    /// wrapping the result in <see cref="LegacyLiveQueryAdapter{T}"/>.
    /// </summary>
    public async Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class
    {
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.Live<T>().SubscribeAsync(ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<T>(sub);
    }

    /// <summary>
    /// [Marten API parity] Starts a live query with a raw SurrealQL WHERE clause.
    /// Delegates to <see cref="ILiveQuerySession.LiveRawQuery{T}"/> internally.
    /// </summary>
    public async Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"LIVE SELECT * FROM `{table}` WHERE {whereClause}";
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.LiveRawQuery<T>(surql, ct: ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<T>(sub);
    }

    /// <summary>
    /// [Marten API parity] Watches events for a specific stream ID (mt_events table).
    /// </summary>
    public async Task<ILiveQuery<object>> WatchStreamAsync(string streamId, CancellationToken ct = default)
    {
        var surql = $"LIVE SELECT * FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}'";
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.LiveRawQuery<object>(surql, ct: ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<object>(sub);
    }

    /// <summary>Start a graph traversal query.</summary>
    public IGraphQuery<T> Graph<T>() where T : class
    {
        return GraphQueryProvider.Graph<T>(this);
    }

    // ===================================================================
    // IQuerySession additions (Marten API parity)
    // ===================================================================

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(int id, CancellationToken ct = default) where T : class
        => LoadByIdentityAsync<T>(id, ct);

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(long id, CancellationToken ct = default) where T : class
        => LoadByIdentityAsync<T>(id, ct);

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => LoadByIdentityAsync<T>(id, ct);

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        return LoadByIdentityAsync<T>(id, ct);
    }

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(string id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(int id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(long id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        return CheckExistsAsyncCore<T>(id, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyAsync<T>(this, ids, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<Guid> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<long> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<int> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

    /// <inheritdoc />
    public async Task<IDocumentMetadata?> MetadataForAsync<T>(T entity, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is IDocumentMetadata existing)
            return existing;

        var table = MetadataDispatch.GetTableName(typeof(T), Options.Schema);
        if (!DocumentIdentityResolver.TryResolve(entity, table, Options.Schema, out var identity))
            return null;

        var fresh = await LoadAsync<T>(identity.Value, ct).ConfigureAwait(false);
        return fresh as IDocumentMetadata;
    }
}
