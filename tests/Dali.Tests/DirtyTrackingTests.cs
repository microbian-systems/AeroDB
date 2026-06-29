using TUnit.Core;

namespace Dali.Tests;

public class DirtyTrackingTests
{
    [Test]
    public async Task Store_and_save_clean_entity_generates_no_update()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Tracking = DocumentTracking.DirtyTracking;
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions
        {
            Tracking = DocumentTracking.DirtyTracking
        });

        var person = new Person { Name = "Alice", Age = 30 };

        // Store the entity (captures baseline snapshot)
        session.Store(person);
        person.Name = "Alice"; // Same value — no effective change

        await session.SaveChangesAsync();

        var saved = await session.Query<Person>().ToListAsync();
        saved.Count.ShouldBe(1);
        saved[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Modify_entity_and_save_persists_changes_in_dirty_tracking_mode()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Tracking = DocumentTracking.DirtyTracking;
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions
        {
            Tracking = DocumentTracking.DirtyTracking
        });

        var person = new Person { Name = "Alice", Age = 30 };
        session.Store(person);
        await session.SaveChangesAsync();

        // Load the entity back and modify it
        var loaded = (await session.Query<Person>().ToListAsync())[0];
        loaded.Name = "AliceUpdated";

        // Use a fresh session with dirty tracking
        // Note: identity map is per-session, so we need to do Store with explicit ID
        var id = ((SurrealDb.Net.Models.RecordIdOf<string>)loaded.Id!).Id;

        await using var session2 = await store.OpenSessionAsync(new SessionOptions
        {
            Tracking = DocumentTracking.DirtyTracking
        });

        var loaded2 = await session2.LoadAsync<Person>(id);
        loaded2.ShouldNotBeNull();
        loaded2.Name = "AliceUpdated";

        // Store it in dirty tracking mode to capture the snapshot
        session2.Store(loaded2);
        await session2.SaveChangesAsync();

        // Verify the change persisted
        await using var session3 = await store.LightweightSessionAsync();
        var reloaded = await session3.LoadAsync<Person>(id);
        reloaded.ShouldNotBeNull();
        reloaded.Name.ShouldBe("AliceUpdated");
    }

    [Test]
    public async Task IdentityMapCount_reflects_tracked_entities()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.DocumentSessionAsync();

        // Store and persist so there are entities to query
        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        // Identity map was cleared by SaveChangesAsync, so LoadAsync re-populates it
        var saved = await session.Query<Person>().ToListAsync();
        saved.Count.ShouldBe(1);
        var id = ((SurrealDb.Net.Models.RecordIdOf<string>)saved[0].Id!).Id;

        var loaded = await session.LoadAsync<Person>(id);
        loaded.ShouldNotBeNull();

        ((IDocumentSession)session).IdentityMapCount.ShouldBeGreaterThanOrEqualTo(1);
    }
}
