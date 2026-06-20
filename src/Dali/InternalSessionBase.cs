using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Dali;

public abstract class InternalSessionBase : IAsyncDisposable
{
    protected readonly ISurrealDbClient Client;
    protected readonly ISurrealDbSession Session;
    protected readonly StoreOptions Options;
    protected readonly Dictionary<Type, Dictionary<string, object>> IdentityMap = new();
    protected bool Disposed;

    protected InternalSessionBase(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options)
    {
        Client = client;
        Session = session;
        Options = options;
    }

    public ISurrealDbQueryable<T> Query<T>() where T : class
    {
        var provider = new SurrealQueryProvider(Session);
        return new SurrealDbQueryable<T>(provider);
    }

    public async Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class
    {
        var table = Snake(typeof(T).Name);
        var result = await Session.RawQuery($"SELECT * FROM {table}:{id} LIMIT 1;", null, ct);
        if (!result.HasErrors && result.Count > 0)
        {
            return result.GetValue<T>(0);
        }
        return null;
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
