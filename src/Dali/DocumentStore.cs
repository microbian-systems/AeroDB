using Microsoft.Extensions.DependencyInjection;
using SurrealDb.Net;

namespace Dali;

public class DocumentStore : IDocumentStore
{
    private ISurrealDbClient? _client;
    private bool _initialized;
    private bool _disposed;

    public DocumentStore(StoreOptions options)
    {
        Options = options;
    }

    public StoreOptions Options { get; }
    public ISurrealDbClient Client => _client
        ?? throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        var ns = Options.Namespace ?? "test";
        var db = Options.Database ?? "test";

        if (Options.ClientFactory is not null)
        {
            _client = Options.ClientFactory();
            await _client.Connect(ct);
            await _client.Use(ns, db, ct);
        }
        else
        {
            var endpoint = Options.Endpoint;
            var user = Options.Username ?? "root";
            var pass = Options.Password ?? "root";

            var surrealOptions = new SurrealDbOptionsBuilder()
                .WithEndpoint(endpoint)
                .WithNamespace(ns)
                .WithDatabase(db)
                .WithUsername(user)
                .WithPassword(pass)
                .Build();

            _client = new SurrealDbClient(surrealOptions);
            await _client.Connect(ct);
            await _client.Use(ns, db, ct);
        }

        // Auto-create event schema if events are enabled
        if (Options.Events.Enabled)
        {
            var schemaManager = new SchemaManager();
            await schemaManager.EnsureEventSchemaAsync(_client, ns, db, ct);
        }
    }

    public async Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct);
        var session = await Client.CreateSession(ct);
        await session.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct);
        var qs = new QuerySession(Client, session, Options);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            qs.TenantId = Options.DefaultTenantId;
        return qs;
    }

    public async Task<IDocumentSession> LightweightSessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct);
        var session = await Client.CreateSession(ct);
        await session.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct);
        var ds = new DocumentSession(Client, session, Options, isDirtyTracking: false);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds.TenantId = Options.DefaultTenantId;
        return ds;
    }

    public async Task<IDocumentSession> DocumentSessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct);
        var session = await Client.CreateSession(ct);
        await session.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct);
        var ds = new DocumentSession(Client, session, Options, isDirtyTracking: true);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds.TenantId = Options.DefaultTenantId;
        return ds;
    }

    private async Task EnsureInitialized(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client is not null)
            await _client.DisposeAsync();
    }
}

public static class Documents
{
    public static IDocumentStore For(Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);
        return new DocumentStore(options);
    }
}
