using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace Dali;

/// <summary>
/// Creates and caches <see cref="ISurrealDbClient"/> instances per tenant when
/// <see cref="TenancyStyle.DatabasePerTenant"/> is active. Each tenant gets its
/// own database: <c>{namespace}_{tenantId}</c>.
/// </summary>
public class DatabasePerTenantSelector : IAsyncDisposable
{
    private readonly StoreOptions _options;
    private readonly ILogger<DatabasePerTenantSelector> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ISurrealDbClient>>> _clients = new();
    private bool _disposed;

    public DatabasePerTenantSelector(StoreOptions options)
    {
        _options = options;
        _logger = options.LoggerFactory?.CreateLogger<DatabasePerTenantSelector>()
            ?? NullLogger<DatabasePerTenantSelector>.Instance;
    }

    /// <summary>
    /// Gets or creates an <see cref="ISurrealDbClient"/> for the specified tenant.
    /// Each tenant is isolated in its own database: <c>{namespace}_{tenantId}</c>.
    /// Uses <c>GetOrAdd</c> with <see cref="Lazy{T}"/> to ensure only one client
    /// is created per tenant, even under concurrent calls.
    /// </summary>
    public async Task<ISurrealDbClient> GetOrCreateClientAsync(string tenantId, CancellationToken ct = default)
    {
        var lazy = _clients.GetOrAdd(tenantId, _ => new Lazy<Task<ISurrealDbClient>>(
            () => CreateClientAsync(tenantId, ct)));

        return await lazy.Value.ConfigureAwait(false);
    }

    private async Task<ISurrealDbClient> CreateClientAsync(string tenantId, CancellationToken ct)
    {
        var ns = _options.Namespace ?? "test";
        var dbName = $"{ns}_{tenantId}";

        ISurrealDbClient client;
        if (_options.ClientFactory is not null)
        {
            // Embedded/factory clients — each tenant gets its own isolated client instance
            client = _options.ClientFactory();
            await client.Connect(ct).ConfigureAwait(false);
            await client.Use(ns, dbName, ct).ConfigureAwait(false);
        }
        else
        {
            // Remote clients — same endpoint, different namespace/database
            var surrealOptions = new SurrealDbOptionsBuilder()
                .WithEndpoint(_options.Endpoint)
                .WithNamespace(ns)
                .WithDatabase(dbName)
                .WithUsername(_options.Username ?? "root")
                .WithPassword(_options.Password ?? "root")
                .Build();

            client = new SurrealDbClient(surrealOptions);
            await client.Connect(ct).ConfigureAwait(false);
            await client.Use(ns, dbName, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Created tenant database: {Database} for tenant {Tenant}", dbName, tenantId);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _clients)
        {
            if (kvp.Value.IsValueCreated)
            {
                try
                {
                    var client = await kvp.Value.Value.ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing client for tenant {TenantId}", kvp.Key);
                }
            }
        }

        _clients.Clear();
    }
}
