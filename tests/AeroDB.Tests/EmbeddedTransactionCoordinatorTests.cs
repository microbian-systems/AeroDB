using AeroDB.Sable;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

public class EmbeddedTransactionCoordinatorTests
{
    [Test]
    public async Task IndependentEmbeddedStores_ConcurrentSavesThenFollowUpTransaction_Succeed()
    {
        var stores = await Task.WhenAll(
            CreateStoreAndSaveAsync("coordinator_first"),
            CreateStoreAndSaveAsync("coordinator_second"));

        try
        {
            await using var session = await stores[0].OpenSessionAsync(
                new SessionOptions { Tracking = DocumentTracking.None });
            session.Store(new Person
            {
                Id = new RecordIdOf<string>("person", "coordinator_follow_up"),
                Name = "Follow up",
            });

            await session.SaveChangesAsync();
        }
        finally
        {
            foreach (var store in stores)
                await store.DisposeAsync();
        }
    }

    private static async Task<IDocumentStore> CreateStoreAndSaveAsync(string id)
    {
        var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new Person
        {
            Id = new RecordIdOf<string>("person", id),
            Name = id,
        });

        await session.SaveChangesAsync();
        return store;
    }
}
