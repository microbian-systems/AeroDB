using AeroDB.Sable;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;

namespace AeroDB.Tests;

public static class TestHarness
{
    private static int _counter;
    private static readonly Lazy<Task<SurrealDbMemoryClient>> SharedRootClient =
        new(ConnectSharedRootClientAsync);

    private static string UniqueNs()
    {
        var id = Interlocked.Increment(ref _counter);
        var guid = Guid.NewGuid().ToString("N")[..8];
        return $"test_{id}_{guid}";
    }

    public static IDocumentStore CreateStore()
    {
        var uniqueId = UniqueNs();
        var store = Documents.For(o =>
        {
            o.ClientFactory = CreateIsolatedClient;
            o.Namespace = uniqueId;
            o.Database = uniqueId;
        });

        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    public static async Task<IDocumentStore> CreateStoreAsync(Action<StoreOptions>? configure = null)
    {
        var uniqueId = UniqueNs();
        var store = Documents.For(o =>
        {
            o.ClientFactory = CreateIsolatedClient;
            configure?.Invoke(o);

            // All test stores share one embedded engine, so the physical namespace
            // must remain unique even when a test uses a conventional logical name
            // such as "test". A configured database name is preserved because
            // database-per-tenant tests depend on that behavior.
            o.Namespace = uniqueId;
            o.Database ??= uniqueId;

            if (o.Encryption.Provider is not null
                || o.Encryption.BlindIndexProvider is not null)
            {
                // The shared embedded test client is created without a logger factory.
                o.Encryption.ExternalClientDisablesProtectedDataLogging = true;
            }
        });

        await store.InitializeAsync();
        return store;
    }

    private static ISurrealDbClient CreateIsolatedClient()
    {
        var rootClient = SharedRootClient.Value.GetAwaiter().GetResult();
        return (ISurrealDbClient)rootClient.CreateSession().GetAwaiter().GetResult();
    }

    private static async Task<SurrealDbMemoryClient> ConnectSharedRootClientAsync()
    {
        var client = new SurrealDbMemoryClient();
        await client.Connect();
        return client;
    }

    internal static async Task DisposeSharedRootClientAsync()
    {
        if (SharedRootClient.IsValueCreated)
        {
            var rootClient = await SharedRootClient.Value;
            await rootClient.DisposeAsync();
        }
    }
}
