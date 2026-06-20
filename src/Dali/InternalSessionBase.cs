using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Dali;

public abstract class InternalSessionBase : IAsyncDisposable
{
    protected readonly ISurrealDbClient Client;
    public ISurrealDbSession Session { get; }
    protected readonly StoreOptions Options;
    protected readonly Dictionary<Type, Dictionary<string, object>> IdentityMap = new();
    protected bool Disposed;

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

    public ISurrealDbQueryable<T> Query<T>() where T : class
    {
        var provider = new SurrealQueryProvider(Session, TenantId);
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
        var table = Snake(typeof(T).Name);
        try
        {
            var rid = new RecordIdOf<string>(table, id);
            var result = await Session.Select<T>(rid, ct);

            // Tenant isolation: if this session is tenant-scoped and the loaded entity
            // has a TenantId property, verify it matches. If not, treat as "not found".
            if (result is not null && !string.IsNullOrEmpty(TenantId))
            {
                var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
                if (tenantProp is not null && tenantProp.CanRead)
                {
                    var entityTenant = tenantProp.GetValue(result) as string;
                    if (!string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
                        return default;
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    protected string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        await Session.CloseSession(DefaultCt);
        if (Session is IAsyncDisposable d)
            await d.DisposeAsync();
    }

    protected static CancellationToken DefaultCt => CancellationToken.None;
}
