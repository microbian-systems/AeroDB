using AeroDB.Sable;
using SurrealDb.Embedded.InMemory;

namespace AeroDB.Tests;

public static class TestHarness
{
    private static int _counter;

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
            o.ClientFactory = () => new NoDisposeSurrealDbMemoryClient();
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
            o.ClientFactory = () => new NoDisposeSurrealDbMemoryClient();
            o.Namespace ??= uniqueId;
            o.Database ??= uniqueId;
            configure?.Invoke(o);
        });

        await store.InitializeAsync();
        return store;
    }
}

/// <summary>
/// Wraps SurrealDbMemoryClient and explicitly re-implements IAsyncDisposable
/// to no-op DisposeAsync, preventing the native engine from being torn down
/// during test runs. This avoids the race condition where native callbacks
/// fire after the native engine is disposed.
/// </summary>
internal sealed class NoDisposeSurrealDbMemoryClient : SurrealDbMemoryClient, IAsyncDisposable
{
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        // Deliberately empty — native engine stays alive for the entire test run.
        // Each store uses unique namespace/database for test isolation.
    }
}
