using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AeroDB.Internals.Cbor;
using AeroDB.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace AeroDB;

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
    private readonly ILogger _logger;

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
    /// Virtual method that determines whether a given type should be tracked
    /// in the identity map. Override in <see cref="DocumentSession"/> to support
    /// <c>UseIdentityMapFor&lt;T&gt;()</c> opt-in types.
    /// </summary>
    internal protected virtual bool ShouldTrackInIdentityMap(Type type) => Tracking >= DocumentTracking.IdentityOnly;

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

    /// <summary>Per-session headers for tracing/correlation.</summary>
    private readonly Dictionary<string, object> _headers = new(StringComparer.Ordinal);

    protected InternalSessionBase(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, DocumentTracking tracking)
    {
        Client = client;
        Session = session;
        Options = options;
        Tracking = tracking;
        Database = new DatabaseInfo(options);
        Json = new JsonLoader(this);
        _logger = options.LoggerFactory?.CreateLogger<InternalSessionBase>() ?? NullLogger<InternalSessionBase>.Instance;
    }

    private sealed class DatabaseInfo : IDatabase
    {
        public DatabaseInfo(StoreOptions options)
        {
            Name = options.Database ?? "test";
            Namespace = options.Namespace;
        }

        public string Name { get; }
        public string? Namespace { get; }
    }

    private sealed class JsonLoader : IJsonLoader
    {
        private readonly InternalSessionBase _session;

        public JsonLoader(InternalSessionBase session) => _session = session;

        public async Task<string?> LoadByIdAsync<T>(string id, CancellationToken ct = default) where T : class
        {
            var results = await _session.RawQueryAsync<T>(
                $"SELECT * FROM {MetadataDispatch.GetTableName(typeof(T))}:`{id.Replace("`", "\\`")}`",
                null, ct).ConfigureAwait(false);
            if (results.Count == 0) return null;
            return System.Text.Json.JsonSerializer.Serialize(results[0], _session.StoreOptions.SerializerOptions);
        }

        public async Task<System.Text.Json.JsonDocument?> LoadDocumentByIdAsync<T>(string id, CancellationToken ct = default) where T : class
        {
            var json = await LoadByIdAsync<T>(id, ct).ConfigureAwait(false);
            if (json is null) return null;
            return System.Text.Json.JsonDocument.Parse(json);
        }

        public async Task WriteById<T>(object id, Microsoft.AspNetCore.Http.HttpContext httpContext, CancellationToken ct = default)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(httpContext);

            var doc = await _session.LoadAsync<T>(id.ToString()!, ct).ConfigureAwait(false);
            if (doc is null)
            {
                httpContext.Response.StatusCode = 404;
                return;
            }

            httpContext.Response.ContentType = "application/json";
            await System.Text.Json.JsonSerializer
                .SerializeAsync(httpContext.Response.Body, doc, _session.StoreOptions.SerializerOptions, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Number of database requests made during this session's lifetime.
    /// Incremented on every LoadAsync, Query, Store, Delete, SaveChangesAsync, ExecuteSqlAsync.
    /// </summary>
    public int RequestCount { get; protected set; }

    /// <summary>
    /// Trace causation ID for distributed tracing — identifies which event/command
    /// caused this session to be opened.
    /// </summary>
    public string? CausationId { get; set; }

    /// <summary>
    /// Trace correlation ID for grouping related operations across services.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Database metadata for the current session.
    /// </summary>
    public IDatabase Database { get; }

    /// <summary>JSON document loader for raw JSON access.</summary>
    public IJsonLoader Json { get; }

    private IEvents? _queryEvents;

    /// <summary>Event store query surface for raw event queries.</summary>
    public virtual IEvents Events => _queryEvents ??= new EventStore(Session, Options);

    /// <summary>
    /// Whether optimistic concurrency is enabled for this session.
    /// Virtual so that <see cref="DocumentSession"/> can override based on
    /// the per-session <see cref="ConcurrencyChecks"/> override.
    /// </summary>
    internal protected virtual bool UseOptimisticConcurrency => Options.UseOptimisticConcurrency;

    /// <summary>
    /// Optional session logger for Marten-compatible diagnostic recording.
    /// When set, all session operations are recorded through this logger.
    /// </summary>
    public IMartenSessionLogger? Logger { get; set; }

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

        if (TryDeserializeMappedPocoResponse<T>(response, out var mapped))
            return mapped;

        return response.GetValue<List<T>>(0) ?? [];
    }

    internal List<T> DeserializeMappedPocoResponse<T>(SurrealDbResponse response, int index = 0)
    {
        var mapping = Options.Schema.Mappings.GetValueOrDefault(typeof(T));
        if (mapping?.IdentityProperty is null)
            return [];

        var records = CborResultReader.ReadPocoResult(response, index);
        return DeserializePocoFromList<T>(records, mapping.IdentityProperty);
    }

    private bool TryDeserializeMappedPocoResponse<T>(SurrealDbResponse response, out List<T> results)
    {
        results = [];

        var mapping = Options.Schema.Mappings.GetValueOrDefault(typeof(T));
        if (mapping?.IdentityProperty is null)
            return false;

        var records = CborResultReader.ReadPocoResult(response, 0);
        results = DeserializePocoFromList<T>(records, mapping.IdentityProperty);
        return true;
    }

    public async Task<int> ExecuteSqlAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        RequestCount++;
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        return response.FirstOk is not null ? 1 : 0;
    }

    protected async Task<bool> CheckExistsAsyncCore<T>(string id, CancellationToken ct) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var sql = $"SELECT id FROM {table}:`{id.Replace("`", "\\`")}`";
        RequestCount++;
        var response = await Session.RawQuery(sql, null, ct).ConfigureAwait(false);
        return response.Count > 0 && !response.HasErrors && response.FirstOk is not null;
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
    /// Checks if <paramref name="type"/> derives from <c>Entity&lt;TId&gt;</c>.
    /// Mirrors the source generator's <c>IsEntitySubclass</c> predicate.
    /// </summary>
    private static bool IsEntityBaseType(Type type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Entity<>))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Loads multiple entities by their IDs with optional tenant filtering.
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
        var table = MetadataDispatch.GetTableName(typeof(T));
        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var loadSession = await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);
        try
        {
            var rid = new RecordIdOf<string>(table, id);

            // Check identity map first
            if (ShouldTrackInIdentityMap(typeof(T)))
            {
                if (IdentityMap.TryGetValue(typeof(T), out var typeMap) && typeMap.TryGetValue(id, out var cached))
                {
                    _logger.LogDebug("LoadAsync<{Type}> identity hit for id={Id}", typeof(T).Name, id);
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
            else if (typeof(IRecord).IsAssignableFrom(typeof(T)))
            {
                result = await loadSession.Select<T>(rid, ct).ConfigureAwait(false);
            }
            else if (IsEntityBaseType(typeof(T)))
            {
                // Entity<TId> without shim — Select<T> works because
                // Dahomey.Cbor deserializes the body field "Id" to the typed Id property
                result = await loadSession.Select<T>(rid, ct).ConfigureAwait(false);
            }
            else
            {
                // POCO path: use RawQuery to get dicts, then JSON round-trip
                result = await LoadPocoAsync<T>(loadSession, table, id, ct).ConfigureAwait(false);
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
                    _logger.LogDebug("Tenant filter applied for LoadAsync<{Type}>: entity tenant '{EntityTenant}' != session tenant '{SessionTenant}'",
                        typeof(T).Name, entityTenant, TenantId);
                    return default;
                }
            }

            // Store in identity map when tracking is enabled (or type opted-in via UseIdentityMapFor)
            if (ShouldTrackInIdentityMap(typeof(T)) && result is not null)
            {
                var typeMap = IdentityMap.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
                typeMap[id] = result;
            }

            // Track original version for optimistic concurrency
            if (result is not null && UseOptimisticConcurrency)
                TrackOriginalVersion(result);

            _logger.LogDebug("Loaded {Type} with id={Id}", typeof(T).Name, id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoadAsync failed for id={Id}", id);
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
    /// Loads a POCO (non-IRecord, non-Entity&lt;TId&gt;) using RawQuery + object-based
    /// deserialization to avoid CBOR RecordId type mismatch issues.
    /// The native SurrealDB <c>id</c> field (e.g., <c>"table:42"</c>) cannot deserialize
    /// to typed identity properties like <c>long</c>, so we read the response as objects,
    /// extract the id, round-trip through JSON, and set identity manually.
    /// </summary>
    private async Task<T?> LoadPocoAsync<T>(ISurrealDbSession session, string table, string id, CancellationToken ct)
        where T : class
    {
        var escapedId = id.Replace("`", "\\`");
        var response = await session.RawQuery(
            $"SELECT * FROM {table}:`{escapedId}`",
            null,
            ct).ConfigureAwait(false);

        // Read raw CBOR data directly to avoid Dahomey.Cbor's ObjectConverter issue with maps.
        var records = CborResultReader.ReadPocoResult(response, 0);
        if (records is not { Count: 1 })
            return null;

        var mapping = Options.Schema.Mappings.GetValueOrDefault(typeof(T));
        return DeserializePocoFromList<T>(records, mapping?.IdentityProperty)[0];
    }

    /// <summary>
    /// Deserializes a list of dictionary records to typed POCOs.
    /// Serializes the dictionaries to JSON as an intermediate step, extracts the native
    /// <c>id</c> field, then deserializes to <c>List&lt;T&gt;</c> and sets identity properties.
    /// </summary>
    internal static List<T> DeserializePocoFromList<T>(
        List<Dictionary<string, object?>> records,
        string? identityProperty = null)
    {
        if (records is null or { Count: 0 }) return [];

        var nativeIds = NormalizePocoIdentityFields<T>(records, identityProperty);

        // Serialize the cleaned dictionaries to JSON, then deserialize to typed POCOs
        var jsonOpts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var json = System.Text.Json.JsonSerializer.Serialize(records);
        var results = System.Text.Json.JsonSerializer.Deserialize<List<T>>(json, jsonOpts);
        if (results is null) return [];

        // Set identity properties from native ids
        for (int j = 0; j < results.Count && j < nativeIds.Length; j++)
        {
            var nativeId = nativeIds[j];
            if (nativeId is not null)
            {
                SetPocoIdentityFromRecordId(results[j]!, nativeId, identityProperty);
            }
        }

        return results;
    }

    private static string?[] NormalizePocoIdentityFields<T>(
        List<Dictionary<string, object?>> records,
        string? identityProperty)
    {
        var nativeIds = new string?[records.Count];
        var idProp = GetPocoIdentityProperty(typeof(T), identityProperty);

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var idEntries = record
                .Where(kvp => string.Equals(kvp.Key, "id", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (idEntries.Length == 0)
            {
                continue;
            }

            var nativeId = idEntries
                .Select(kvp => ExtractRecordIdString(kvp.Value))
                .FirstOrDefault(value => value?.Contains(':', StringComparison.Ordinal) == true)
                ?? ExtractRecordIdString(idEntries[0].Value);

            foreach (var (key, _) in idEntries)
            {
                record.Remove(key);
            }

            nativeIds[i] = nativeId;

            if (nativeId is not null && idProp is not null)
            {
                record[idProp.Name] = ConvertRecordIdToIdentityValue(nativeId, idProp.PropertyType);
            }
        }

        return nativeIds;
    }

    /// <summary>
    /// Extracts a SurrealDB record ID string from a CBOR-deserialized id value.
    /// Handles both string format (<c>"table:id"</c>) and array format
    /// (<c>["table", id_value]</c> — SurrealDB RecordId encoding).
    /// </summary>
    private static string? ExtractRecordIdString(object? idValue)
    {
        if (idValue is string s)
            return s;

        // SurrealDB RecordId array format: ["table_name", id_value]
        if (idValue is List<object?> { Count: >= 2 } list)
        {
            var table = FormatRecordIdPart(list[0]) ?? "";
            var id = FormatRecordIdPart(list[1]) ?? "";
            return string.IsNullOrEmpty(id) ? table : $"{table}:{id}";
        }

        return idValue?.ToString();
    }

    /// <summary>
    /// Sets the identity property on a POCO from a SurrealDB record ID string
    /// (e.g., <c>"table:42"</c> → sets <c>Id = 42</c> for <c>long</c> identity).
    /// Supports <c>long</c>, <c>int</c>, <c>ulong</c>, <c>uint</c>,
    /// <c>string</c>, and <c>Guid</c> identity types.
    /// </summary>
    private static void SetPocoIdentityFromRecordId(
        object entity,
        string nativeId,
        string? identityProperty)
    {
        var entityType = entity.GetType();
        var idProp = GetPocoIdentityProperty(entityType, identityProperty);
        if (idProp is null || !idProp.CanWrite)
            return;

        idProp.SetValue(entity, ConvertRecordIdToIdentityValue(nativeId, idProp.PropertyType));
    }

    private static PropertyInfo? GetPocoIdentityProperty(Type entityType, string? identityProperty)
    {
        if (!string.IsNullOrWhiteSpace(identityProperty))
        {
            return entityType.GetProperty(identityProperty);
        }

        return entityType.GetProperty("Id");
    }

    private static object? ConvertRecordIdToIdentityValue(string nativeId, Type propertyType)
    {
        var lastColon = nativeId.LastIndexOf(':');
        var idStr = lastColon >= 0 ? nativeId[(lastColon + 1)..] : nativeId;
        var propType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        return propType switch
        {
            _ when propType == typeof(long) => long.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(int) => int.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(ulong) => ulong.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(uint) => uint.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(string) => idStr,
            _ when propType == typeof(Guid) => Guid.Parse(idStr),
            _ when propType == typeof(byte) => byte.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(short) => short.Parse(idStr, System.Globalization.CultureInfo.InvariantCulture),
            _ when propType == typeof(DateTime) => System.DateTime.Parse(
                idStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            _ => System.Convert.ChangeType(idStr, propType, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static string? FormatRecordIdPart(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
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
    /// Get the known version for a tracked entity. Returns the version at load/store time,
    /// or null if the entity is not tracked or has no version field.
    /// </summary>
    public long? VersionFor<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (_originalVersions.TryGetValue(entity, out var version))
            return version;

        // Check if the entity has a version we can read even if not tracked
        var v = GetVersion(entity);
        return v >= 0 ? v : null;
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

    public virtual async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;

        // Dispose all forked sessions first (they may depend on primary)
        foreach (var kvp in _forkedSessionCache)
        {
            try
            {
                if (kvp.Value.IsValueCreated)
                {
                    var forked = await kvp.Value.Value.ConfigureAwait(false);
                    await forked.CloseSession(DefaultCt).ConfigureAwait(false);
                    if (forked is IAsyncDisposable fd)
                        await fd.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException) { /* Already disposed — safe to ignore */ }
        }
        _forkedSessionCache.Clear();

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
    /// Execute a compiled query and return the result as a JSON string.
    /// Returns null if no matching document is found.
    /// </summary>
    public async Task<string?> ToJsonOne<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class
    {
        var result = await QueryAsync(compiledQuery, ct).ConfigureAwait(false);
        if (result is null || result.Equals(default(TOut)))
            return null;
        return System.Text.Json.JsonSerializer.Serialize(result, Options.SerializerOptions);
    }

    /// <summary>
    /// Execute a compiled list query and return results as a JSON array string.
    /// Returns an empty array "[]" if no results are found.
    /// </summary>
    public async Task<string> ToJsonMany<TDoc, TOut>(ICompiledListQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class
    {
        var results = await QueryAsync(compiledQuery, ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Serialize(results, Options.SerializerOptions);
    }

    /// <summary>
    /// Execute a compiled query and write the JSON result directly to a stream.
    /// Nothing is written if no matching document is found.
    /// </summary>
    public async Task StreamJsonOne<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, Stream destination, CancellationToken ct = default)
        where TDoc : class
    {
        var result = await QueryAsync(compiledQuery, ct).ConfigureAwait(false);
        if (result is null || result.Equals(default(TOut)))
            return;
        await System.Text.Json.JsonSerializer.SerializeAsync(destination, result, Options.SerializerOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute a compiled list query and write the JSON array result directly to a stream.
    /// Writes an empty array "[]" if no results are found.
    /// </summary>
    public async Task StreamJsonMany<TDoc, TOut>(ICompiledListQuery<TDoc, TOut> compiledQuery, Stream destination, CancellationToken ct = default)
        where TDoc : class
    {
        var results = await QueryAsync(compiledQuery, ct).ConfigureAwait(false);
        await System.Text.Json.JsonSerializer.SerializeAsync(destination, results, Options.SerializerOptions, ct).ConfigureAwait(false);
    }

    // ── ITEM 2: StreamJson<T> ─────────────────────────────────────

    /// <inheritdoc />
    public async Task StreamJson<T>(Stream destination, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class
    {
        var results = await RawQueryAsync<T>(sql, parameters, ct).ConfigureAwait(false);
        await System.Text.Json.JsonSerializer.SerializeAsync(destination, results, Options.SerializerOptions, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StreamJson<T>(Stream destination, string placeholder, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class
    {
        var resolvedSql = sql.Replace(placeholder, Database.Name);
        if (!string.IsNullOrEmpty(TenantId))
            resolvedSql = resolvedSql.Replace("{tenant}", TenantId);
        await StreamJson<T>(destination, resolvedSql, parameters, ct).ConfigureAwait(false);
    }

    // ── ITEM 3: QueryAsync placeholder variant ─────────────────────

    /// <inheritdoc />
    public async Task<List<T>> QueryAsync<T>(string placeholder, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class
    {
        var resolvedSql = sql.Replace(placeholder, Database.Name);
        if (!string.IsNullOrEmpty(TenantId))
            resolvedSql = resolvedSql.Replace("{tenant}", TenantId);
        return await RawQueryAsync<T>(resolvedSql, parameters, ct).ConfigureAwait(false);
    }

    // ── ITEM 4: Advanced SQL addons ────────────────────────────────

    /// <inheritdoc />
    public IAsyncEnumerable<T> StreamAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class
    {
        // Delegate to the existing AeroDBAdvancedSql implementation
        return new AeroDBAdvancedSql(this).StreamAsync<T>(sql, parameters, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(T1, T2)> StreamAsync<T1, T2>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        where T1 : class where T2 : class
    {
        var results1 = await RawQueryAsync<T1>(sql, parameters, ct).ConfigureAwait(false);
        var results2 = await RawQueryAsync<T2>(sql, parameters, ct).ConfigureAwait(false);
        var max = Math.Min(results1.Count, results2.Count);
        for (int i = 0; i < max; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (results1[i], results2[i]);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(T1, T2, T3)> StreamAsync<T1, T2, T3>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class
    {
        var results1 = await RawQueryAsync<T1>(sql, parameters, ct).ConfigureAwait(false);
        var results2 = await RawQueryAsync<T2>(sql, parameters, ct).ConfigureAwait(false);
        var results3 = await RawQueryAsync<T3>(sql, parameters, ct).ConfigureAwait(false);
        var max = Math.Min(Math.Min(results1.Count, results2.Count), results3.Count);
        for (int i = 0; i < max; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (results1[i], results2[i], results3[i]);
        }
    }

    /// <inheritdoc />
    public async Task<List<(T1, T2)>> QueryAsync<T1, T2>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class
    {
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors)
            throw new InvalidOperationException("SurrealDB multi-statement query error.");

        var list1 = response.GetValue<List<T1>>(0) ?? [];
        var list2 = response.GetValue<List<T2>>(1) ?? [];
        return list1.Zip(list2, (a, b) => (a, b)).ToList();
    }

    /// <inheritdoc />
    public async Task<List<(T1, T2, T3)>> QueryAsync<T1, T2, T3>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class
    {
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors)
            throw new InvalidOperationException("SurrealDB multi-statement query error.");

        var list1 = response.GetValue<List<T1>>(0) ?? [];
        var list2 = response.GetValue<List<T2>>(1) ?? [];
        var list3 = response.GetValue<List<T3>>(2) ?? [];

        // Zip three lists by index (min count of all three)
        var count = Math.Min(list1.Count, Math.Min(list2.Count, list3.Count));
        var result = new List<(T1, T2, T3)>(count);
        for (int i = 0; i < count; i++)
            result.Add((list1[i], list2[i], list3[i]));
        return result;
    }

    /// <summary>
    /// Creates a batch query that can execute multiple compiled queries
    /// in a single SurrealDB multi-statement round trip.
    /// </summary>
    public IBatchedQuery CreateBatchQuery() => new BatchedQuery(this);

    /// <inheritdoc />
    public Task<List<T>> QueryByPlanAsync<T>(string plan, CancellationToken ct = default) where T : class
        => RawQueryAsync<T>(plan, null, ct);

    // ── QueueSqlCommand (Marten parity) ───────────────────────────────

    internal readonly List<(string Sql, IReadOnlyDictionary<string, object?>? Parameters)> QueuedSqlCommands = new();

    /// <inheritdoc />
    public void QueueSqlCommand(string placeholder, string sql, params object[] parameters)
    {
        if (string.IsNullOrEmpty(placeholder))
            throw new ArgumentException("Placeholder cannot be null or empty.", nameof(placeholder));
        if (string.IsNullOrEmpty(sql))
            throw new ArgumentException("SQL cannot be null or empty.", nameof(sql));

        var resolvedSql = sql.Replace(placeholder, Database.Name);
        if (!string.IsNullOrEmpty(TenantId))
            resolvedSql = resolvedSql.Replace("{tenant}", TenantId);

        IReadOnlyDictionary<string, object?>? paramDict = null;
        if (parameters is { Length: > 0 })
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < parameters.Length; i++)
                dict[$"p{i}"] = parameters[i];
            paramDict = dict;
        }

        QueuedSqlCommands.Add((resolvedSql, paramDict));
    }

    /// <summary>
    /// Executes all queued SQL commands (from <see cref="QueueSqlCommand"/>)
    /// in order. Called during <see cref="DocumentSession.SaveChangesAsync"/>.
    /// </summary>
    internal protected async Task ExecuteQueuedSqlCommandsAsync(CancellationToken ct)
    {
        foreach (var (sql, parameters) in QueuedSqlCommands)
        {
            await ExecuteSqlAsync(sql, parameters, ct).ConfigureAwait(false);
        }
        QueuedSqlCommands.Clear();
    }

    /// <summary>Set a per-session header for tracing/correlation.</summary>
    public void SetHeader(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _headers[key] = value;
    }

    /// <summary>Get a per-session header. Returns null if not found.</summary>
    public object? GetHeader(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _headers.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>The underlying document store that created this session.</summary>
    public IDocumentStore? DocumentStore { get; set; }

    // ── ITEM 4b: Search convenience methods (Marten parity) ─────────

    /// <summary>
    /// Gets the string property names of a type for building generic search queries.
    /// </summary>
    private static string[] GetStringPropertyNames<T>() where T : class
    {
        return typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead)
            .Select(p => p.Name)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> SearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class
    {
        var stringProps = GetStringPropertyNames<T>();
        if (stringProps.Length == 0) return [];

        var table = MetadataDispatch.GetTableName(typeof(T));
        var escaped = searchTerm.Replace("'", "\\'");
        var analyzerClause = analyzer is not null ? $" ANALYZER {analyzer}" : "";

        // Build OR'd search conditions across all string properties using the @ operator
        var conditions = stringProps.Select(field => $"{field} @0@ '{escaped}'");
        var whereClause = string.Join(" OR ", conditions);
        var sql = $"SELECT *, search::score(0) AS _score FROM `{table}` WHERE ({whereClause}){analyzerClause} ORDER BY _score DESC";
        return await RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> PlainTextSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class
        => SearchAsync<T>(searchTerm, analyzer, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> PhraseSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class
    {
        var stringProps = GetStringPropertyNames<T>();
        if (stringProps.Length == 0) return [];

        var table = MetadataDispatch.GetTableName(typeof(T));
        var escaped = searchTerm.Replace("'", "\\'");
        var analyzerClause = analyzer is not null ? $" ANALYZER {analyzer}" : "";

        // Phrase search: uses index 2 but same @ operator; the difference is
        // that the search string is treated as a phrase (exact sequence of terms).
        var conditions = stringProps.Select(field => $"{field} @2@ '{escaped}'");
        var whereClause = string.Join(" OR ", conditions);
        var sql = $"SELECT *, search::score(2) AS _score FROM `{table}` WHERE ({whereClause}){analyzerClause} ORDER BY _score DESC";
        return await RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> WebStyleSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class
    {
        var stringProps = GetStringPropertyNames<T>();
        if (stringProps.Length == 0) return [];

        var table = MetadataDispatch.GetTableName(typeof(T));
        var escaped = searchTerm.Replace("'", "\\'");
        var analyzerClause = analyzer is not null ? $" ANALYZER {analyzer}" : "";

        // Web-style search: uses index 3, which typically enables fuzzy matching
        var conditions = stringProps.Select(field => $"{field} @3@ '{escaped}'");
        var whereClause = string.Join(" OR ", conditions);
        var sql = $"SELECT *, search::score(3) AS _score FROM `{table}` WHERE ({whereClause}){analyzerClause} ORDER BY _score DESC";
        return await RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> PrefixSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class
    {
        var stringProps = GetStringPropertyNames<T>();
        if (stringProps.Length == 0) return [];

        var table = MetadataDispatch.GetTableName(typeof(T));
        var escaped = searchTerm.Replace("'", "\\'");

        // Prefix search: uses string::starts_with which works without FULLTEXT indexes
        var conditions = stringProps.Select(field => $"string::starts_with(string::lowercase({field}), '{escaped.ToLowerInvariant()}')");
        var whereClause = string.Join(" OR ", conditions);
        var sql = $"SELECT * FROM `{table}` WHERE {whereClause}";
        return await RawQueryAsync<T>(sql, null, ct).ConfigureAwait(false);
    }

    // ── ITEM 5: QueryForNonStaleData (Marten parity) ─────────────────

    /// <summary>
    /// Queries the highest event sequence processed by the async daemon (across all shards).
    /// Returns 0 if no progress record exists or the daemon is not running.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private async Task<long> QueryDaemonHighWaterMarkAsync()
    {
        try
        {
            var response = await Session.RawQuery(
                "SELECT max(last_version) AS max_seq FROM mt_projection_progress GROUP ALL;",
                null, CancellationToken.None).ConfigureAwait(false);
            if (!response.HasErrors && response.Count > 0)
            {
                var raw = response.GetValue<List<object>>(0);
                if (raw is { Count: > 0 })
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(raw, _snakeOptions);
                    var dict = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json, _snakeOptions);
                    if (dict is { Count: > 0 } && dict[0].TryGetValue("max_seq", out var seq) && seq is not null)
                        return Convert.ToInt64(seq);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daemon query failed (table may not exist yet)");
        }
        return 0;
    }

    private async Task<long> QueryMaxEventSequenceAsync()
    {
        try
        {
            var response = await Session.RawQuery(
                "SELECT max(sequence) AS max_seq FROM mt_events GROUP ALL;",
                null, CancellationToken.None).ConfigureAwait(false);
            if (!response.HasErrors && response.Count > 0)
            {
                var raw = response.GetValue<List<object>>(0);
                if (raw is { Count: > 0 })
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(raw, _snakeOptions);
                    var dict = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json, _snakeOptions);
                    if (dict is { Count: > 0 } && dict[0].TryGetValue("max_seq", out var seq) && seq is not null)
                        return Convert.ToInt64(seq);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daemon query failed (table may not exist yet)");
        }
        return 0;
    }

    /// <inheritdoc />
    public async Task<ISurrealDbQueryable<T>> QueryForNonStaleData<T>(TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        var maxSeq = await QueryMaxEventSequenceAsync().ConfigureAwait(false);
        if (maxSeq == 0)
            return Query<T>(); // No events — nothing to wait for

        while (DateTime.UtcNow < deadline)
        {
            var daemonSeq = await QueryDaemonHighWaterMarkAsync().ConfigureAwait(false);
            if (daemonSeq >= maxSeq)
                return Query<T>(); // Daemon is caught up
            await Task.Delay(100).ConfigureAwait(false);
        }

        return Query<T>(); // Timeout — return anyway
    }

    /// <inheritdoc />
    public Task<ISurrealDbQueryable<T>> QueryForNonStaleData<T>(TimeSpan timeout, StaleDataMode mode) where T : class
    {
        if (mode == StaleDataMode.AllowStale)
            return Task.FromResult(Query<T>());
        return QueryForNonStaleData<T>(timeout);
    }
}
