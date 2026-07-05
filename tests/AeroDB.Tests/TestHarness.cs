using AeroDB;
using SurrealDb.Embedded.InMemory;

namespace AeroDB.Tests;

public static class TestHarness
{
    private static string UniqueNs() => $"test_{Guid.NewGuid():N}"[..13];

    public static IDocumentStore CreateStore()
    {
        var uniqueId = UniqueNs();
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
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
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.Namespace ??= uniqueId;
            o.Database ??= uniqueId;
            configure?.Invoke(o);
        });

        await store.InitializeAsync();
        return store;
    }
}
