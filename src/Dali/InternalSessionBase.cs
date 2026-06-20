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
    protected readonly Dictionary<Type, Dictionary<string, object>> IdentityMap = new();
    protected bool Disposed;

    /// <summary>
    /// Tracks the original version of each entity for optimistic concurrency checks.
    /// Key is entity instance (reference equality), value is the version at load/store time.
    /// </summary>
    private readonly Dictionary<object, long> _originalVersions = new();

    /// <summary>
    /// The tenant ID for this session (null if no tenancy is configured).
    /// </summary>
    public string? TenantId { get; set; }

    protected InternalSessionBase(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options)
    {
        Client = client;
        Session = session;
        Options = options;
    }

    protected ILogger<T> CreateLogger<T>() =>
        Options.LoggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;

    public async Task<List<T>> RawQueryAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        return response.GetValue<List<T>>(0) ?? [];
    }

    public async Task<int> ExecuteSqlAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        var response = await Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        return response.FirstOk is not null ? 1 : 0;
    }

    public ISurrealDbQueryable<T> Query<T>() where T : class
    {
        var provider = new SurrealQueryProvider(Session, Options, TenantId);
        return new SurrealDbQueryable<T>(provider);
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

    public async Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class
    {
        var logger = CreateLogger<InternalSessionBase>();
        var table = MetadataDispatch.GetTableName(typeof(T));
        try
        {
            var rid = new RecordIdOf<string>(table, id);
            var result = await Session.Select<T>(rid, ct).ConfigureAwait(false);

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
}
