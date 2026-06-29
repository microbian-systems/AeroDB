using System.Collections.Concurrent;
using System.Reflection;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Dali;

public abstract class InternalSessionBase : IAsyncDisposable
{
    protected readonly ISurrealDbClient Client;
    public ISurrealDbSession Session { get; }
    protected readonly StoreOptions Options;
    internal StoreOptions StoreOptions => Options;
    protected readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, object>> IdentityMap = new();

    /// <summary>
    /// JSON snapshots of entities at the time they were loaded/stored, used by
    /// dirty-tracking to detect modifications on SaveChangesAsync.
    /// </summary>
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, string>> _identityMapSnapshots = new();

    protected DocumentTracking Tracking { get; }
    protected bool Disposed;

    /// <summary>Total number of entities currently tracked in the identity map.</summary>
    public int IdentityMapCount => IdentityMap.Values.Sum(m => m.Count);

    /// <summary>Per-type breakdown of tracked entities for diagnostics.</summary>
    public IReadOnlyDictionary<Type, int> IdentityMapKeys => IdentityMap.ToDictionary(k => k.Key, v => v.Value.Count);

    /// <summary>
    /// Tracks the original version of each entity for optimistic concurrency checks.
    /// Key is entity instance (reference equality), value is the version at load/store time.
    /// </summary>
    private readonly Dictionary<object, long> _originalVersions = new();

    /// <summary>
    /// The tenant ID for this session (null if no tenancy is configured).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Whether this session uses dirty-tracking mode. When true, the identity map tracks
    /// loaded documents and detects modifications on SaveChangesAsync.
    /// Current implementation: flag is wired for future use; full dirty-tracking Phase 15+.
    /// </summary>
    internal bool IsDirtyTracking => Options?.Tracking == DocumentTracking.DirtyTracking;

    /// <summary>
    /// The current user/identity for audit metadata (e.g., <see cref="IDocumentMetadata.LastModifiedBy"/>).
    /// Set this before <c>SaveChangesAsync</c> to populate <see cref="IDocumentMetadata.LastModifiedBy"/>
    /// via <see cref="Diagnostics.DocumentMetadataListener"/>.
    /// </summary>
    public string? CurrentUser { get; set; }

    /// <summary>
    /// Caches forked sessions per schema (database) name so each schema
    /// only creates one forked session per <c>InternalSessionBase</c> lifetime.
    /// See <see cref="GetSessionForSchemaAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<ISurrealDbSession>>> _forkedSessionCache = new();

    protected InternalSessionBase(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, DocumentTracking tracking)
    {
        Client = client;
        Session = session;
        Options = options;
        Tracking = tracking;
    }

    /// <summary>
    /// Number of database requests made during this session's lifetime.
    /// Incremented on every LoadAsync, Query, Store, Delete, SaveChangesAsync, ExecuteSqlAsync.
    /// </summary>
    public long RequestCount { get; protected set; }

    /// <summary>
    /// Combined listener pipeline: store-level + session-level listeners.
    /// Populated by <see cref="DocumentSession"/> constructor.
    /// </summary>
    internal List<IDocumentSessionListener> SessionListeners { get; } = new();

    protected ILogger<T> CreateLogger<T>() =>
        Options.LoggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;

    public async Task<List<T>> RawQueryAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        RequestCount++;
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        return response.GetValue<List<T>>(0) ?? [];
    }

    public async Task<int> ExecuteSqlAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        RequestCount++;
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        return response.FirstOk is not null ? 1 : 0;
    }

    public ISurrealDbQueryable<T> Query<T>() where T : class
    {
        var provider = new SurrealQueryProvider(Session, this, Options, TenantId);
        return new SurrealDbQueryable<T>(provider);
    }

    /// <summary>
    /// Returns a session scoped to the specified schema (database).
    /// When <paramref name="schemaName"/> is null, returns the parent <see cref="Session"/>.
    /// Otherwise, forks a new session via <see cref="ISurrealDbSession.ForkSession"/>,
    /// calls <c>Use(ns, schemaName)</c>, and caches the result.
    /// </summary>
    internal protected async Task<ISurrealDbSession> GetSessionForSchemaAsync(string? schemaName, CancellationToken ct = default)
    {
        if (schemaName is null)
            return Session;

        var lazy = _forkedSessionCache.GetOrAdd(schemaName, _ => new Lazy<Task<ISurrealDbSession>>(
            () => CreateForkedSessionAsync(schemaName, ct)));

        return await lazy.Value.ConfigureAwait(false);
    }

    private async Task<ISurrealDbSession> CreateForkedSessionAsync(string schemaName, CancellationToken ct)
    {
        var ns = Options.Namespace ?? "test";
        var forked = await Session.ForkSession(ct).ConfigureAwait(false);
        await forked.Use(ns, schemaName, ct).ConfigureAwait(false);
        return forked;
    }

    /// <summary>
    /// Sets the tenant context for this session, scoping all subsequent operations to the given tenant.
    /// </summary>
    public void SetTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
    }

    /// <summary>
    /// Clears the tenant context from this session.
    /// </summary>
    public void ClearTenant()
    {
        TenantId = null;
    }

    /// <summary>
    /// Fetch the latest projected aggregate document for the given stream
    /// without replaying events. Delegates to <see cref="LoadAsync{T}"/> because
    /// the stream ID IS the projected document ID for <see cref="SingleStreamProjection{T}"/>.
    /// </summary>
    public virtual Task<T?> FetchLatest<T>(string streamId, CancellationToken ct = default) where T : class
        => LoadAsync<T>(streamId, ct);

    public async Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class
    {
        RequestCount++;
        var logger = CreateLogger<InternalSessionBase>();
        var table = MetadataDispatch.GetTableName(typeof(T));
        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var loadSession = await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);
        try
        {
            var rid = new RecordIdOf<string>(table, id);

            // Check identity map first
            if (Tracking >= DocumentTracking.IdentityOnly)
            {
                if (IdentityMap.TryGetValue(typeof(T), out var typeMap) && typeMap.TryGetValue(id, out var cached))
                {
                    logger.LogDebug("LoadAsync<{Type}> identity hit for id={Id}", typeof(T).Name, id);
                    return (T?)cached;
                }
            }

            // Try shim-based deserialization for IEntity<TId> types
            T? result;
            var shimType = MetadataRegistry.GetShimType(typeof(T));
            if (shimType is not null)
            {
                result = await DeserializeViaShimAsync<T>(loadSession, shimType, rid, ct).ConfigureAwait(false);
            }
            else
            {
                result = await loadSession.Select<T>(rid, ct).ConfigureAwait(false);
            }

            // Tenant isolation: if this session is tenant-scoped and the loaded entity
            // has a TenantId property, verify it matches. If not, treat as "not found".
            // DatabasePerTenant isolates at the database level — no entity-level check needed.
            if (result is not null && !string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
            {
                string? entityTenant;
                var meta = MetadataRegistry.TryGet<T>();
                if (meta is not null)
                {
                    entityTenant = meta.GetTenantId(result);
                }
                else
                {
                    var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
                    entityTenant = tenantProp?.GetValue(result) as string;
                }
                
                if (!string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
                {
                    logger.LogDebug("Tenant filter applied for LoadAsync<{Type}>: entity tenant '{EntityTenant}' != session tenant '{SessionTenant}'",
                        typeof(T).Name, entityTenant, TenantId);
                    return default;
                }
            }

            // Store in identity map when tracking is enabled
            if (Tracking >= DocumentTracking.IdentityOnly && result is not null)
            {
                var typeMap = IdentityMap.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
                typeMap[id] = result;
            }

            // Track original version for optimistic concurrency
            if (result is not null && Options.UseOptimisticConcurrency)
                TrackOriginalVersion(result);

            logger.LogDebug("Loaded {Type} with id={Id}", typeof(T).Name, id);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deserializes a SurrealDB response via the source-generated shim type,
    /// then materializes to the entity via <c>ToEntity()</c>.
    /// </summary>
    private async Task<T?> DeserializeViaShimAsync<T>(
        ISurrealDbSession session, Type shimType, RecordId rid, CancellationToken ct) where T : class
    {
        // Cache the MethodInfo for Select<TShim> — reflection once per T
        var selectMethod = typeof(ISurrealDbSession)
            .GetMethod(nameof(ISurrealDbSession.Select), 1, [typeof(RecordId), typeof(CancellationToken)])!
            .MakeGenericMethod(shimType);

        var task = selectMethod.Invoke(session, [rid, ct]) as Task;
        if (task is null) return null;

        await task.ConfigureAwait(false);

        // Extract Result property via reflection
        var resultProp = task.GetType().GetProperty("Result");
        var shim = resultProp?.GetValue(task);
        if (shim is null) return null;

        // Call ToEntity()
        var toEntityMethod = shimType.GetMethod("ToEntity", Type.EmptyTypes);
        if (toEntityMethod is null) return null;

        return (T?)toEntityMethod.Invoke(shim, null);
    }

    /// <summary>
    /// Captures the entity's current version so it can be checked later during
    /// <c>SaveChangesAsync</c>. Only tracks entities that have a version field
    /// (via <see cref="IVersioned"/> or <see cref="VersionAttribute"/>).
    /// </summary>
    protected void TrackOriginalVersion(object entity)
    {
        var version = GetVersion(entity);
        if (version >= 0)
            _originalVersions[entity] = version;
    }

    /// <summary>
    /// Returns the current version value from the entity, or -1 if no version
    /// field is found. <see cref="VersionAttribute"/> takes precedence over
    /// <see cref="IVersioned"/> when both are present on the same type.
    /// Uses <see cref="MetadataDispatch.GetVersionFieldName"/> for fast property name
    /// resolution when generated metadata is available, with reflection fallback.
    /// </summary>
    protected long GetVersion(object entity)
    {
        var entityType = entity.GetType();

        // Use generated metadata accessor when available — zero reflection
        if (MetadataRegistry.TryGet(entityType) is ITypeMetadata meta && meta.GetVersionAccessor is not null)
            return meta.GetVersionAccessor(entity);

        // Fallback: use metadata dispatch for field name, then reflection
        var versionFieldName = MetadataDispatch.GetVersionFieldName(entityType);

        if (versionFieldName is not null)
        {
            var prop = entityType.GetProperty(versionFieldName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is not null)
                return (long)prop.GetValue(entity)!;

            // If the named property isn't found (e.g., interface mapping), fall through
        }

        // Check for IVersioned directly as final fallback
        if (entity is IVersioned versioned)
            return versioned.Version;

        return -1;
    }

    /// <summary>
    /// Increments the version field on the entity (if it has one).
    /// Uses <see cref="MetadataDispatch.GetVersionFieldName"/> for fast resolution
    /// when generated metadata is available, with reflection fallback.
    /// </summary>
    protected void IncrementVersion(object entity)
    {
        var entityType = entity.GetType();

        // Use generated metadata accessor when available — zero reflection
        if (MetadataRegistry.TryGet(entityType) is ITypeMetadata meta && meta.SetVersionAccessor is not null)
        {
            var current = meta.GetVersionAccessor?.Invoke(entity) ?? -1;
            meta.SetVersionAccessor(entity, current + 1);
            return;
        }

        // Fallback: use metadata dispatch for field name, then reflection
        var versionFieldName = MetadataDispatch.GetVersionFieldName(entityType);

        if (versionFieldName is not null)
        {
            var prop = entityType.GetProperty(versionFieldName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is not null)
            {
                prop.SetValue(entity, (long)prop.GetValue(entity)! + 1);
                return;
            }
        }

        if (entity is IVersioned versioned)
            versioned.Version++;
    }

    /// <summary>
    /// Removes the version tracking entry for the given entity.
    /// </summary>
    protected void RemoveOriginalVersion(object entity)
    {
        _originalVersions.Remove(entity);
    }

    /// <summary>
    /// Gets the tracked original version for an entity, or 0 if not tracked.
    /// </summary>
    protected long GetTrackedVersion(object entity)
    {
        return _originalVersions.GetValueOrDefault(entity, 0);
    }

    /// <summary>Remove a document from the identity map by ID. Does NOT delete from the database.</summary>
    public void Eject<T>(string id) where T : class
    {
        if (IdentityMap.TryGetValue(typeof(T), out var typeMap))
            typeMap.TryRemove(id, out _);
    }

    /// <summary>Remove all documents of a given type from the identity map.</summary>
    public void EjectAll<T>() where T : class
    {
        IdentityMap.TryRemove(typeof(T), out _);
    }

    /// <summary>Remove ALL documents from the identity map.</summary>
    public void EjectAll()
    {
        IdentityMap.Clear();
    }

    /// <summary>Captures a JSON snapshot of an entity for dirty-tracking comparison.</summary>
    internal void CaptureSnapshot(Type type, string id, object entity)
    {
        var jsonOptions = Options.SerializerOptions ?? new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var snapshots = _identityMapSnapshots.GetOrAdd(type, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        snapshots[id] = System.Text.Json.JsonSerializer.Serialize(entity, jsonOptions);
    }

    /// <summary>Checks if an entity has changed since its last snapshot.</summary>
    internal bool HasChanged(Type type, string id, object entity)
    {
        if (!_identityMapSnapshots.TryGetValue(type, out var snapshots) || !snapshots.TryGetValue(id, out var snapshot))
            return true; // No snapshot = assume changed

        var jsonOptions = Options.SerializerOptions ?? new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var current = System.Text.Json.JsonSerializer.Serialize(entity, jsonOptions);
        return current != snapshot;
    }

    /// <summary>Clears all entity snapshots used for dirty-tracking comparison.</summary>
    internal void ClearSnapshots() => _identityMapSnapshots.Clear();

    internal string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        await Session.CloseSession(DefaultCt).ConfigureAwait(false);
        if (Session is IAsyncDisposable d)
            await d.DisposeAsync().ConfigureAwait(false);
    }

    protected static CancellationToken DefaultCt => CancellationToken.None;

    /// <summary>
    /// Executes a Marten-compatible interface-based compiled query.
    /// Delegates to <see cref="CompiledQueryPlanner.QueryAsync{TDoc,TOut}"/>.
    /// </summary>
    public Task<TOut> QueryAsync<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class
        => CompiledQueryPlanner.QueryAsync<TDoc, TOut>(this, compiledQuery, ct);

    /// <summary>
    /// Creates a batch query that can execute multiple compiled queries
    /// in a single SurrealDB multi-statement round trip.
    /// </summary>
    public IBatchedQuery CreateBatchQuery() => new BatchedQuery(this);
}
