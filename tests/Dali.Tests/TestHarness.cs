using SurrealDb.Embedded.InMemory;

namespace Dali.Tests;

public static class TestHarness
{
    public static IDocumentStore CreateStore()
    {
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
        });

        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    public static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
        });

        await store.InitializeAsync();
        return store;
    }
}
