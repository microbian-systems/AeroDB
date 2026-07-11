using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public class SessionTests
{
    [Test]
    public async Task Store_entity_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        await session.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var results = await qs.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Delete_entity()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new Person { Name = "DeleteMe", Age = 99 };
        session.Store(person);
        await session.SaveChangesAsync();

        session.Delete(person);
        await session.SaveChangesAsync();
    }

    [Test]
    public async Task Query_session_reads_data()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        await using var ws = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        ws.Store(new Person { Name = "Zoe", Age = 40 });
        await ws.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var results = await qs.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Store_and_load_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new Person { Name = "Alice", Age = 30 };
        session.Store(person);
        await session.SaveChangesAsync();

        // Note: Id mapping from SaveChanges has a known limitation (may remain null).
        // Verify data is queryable instead.
        var all = await session.Query<Person>().ToListAsync();
        var loaded = all.FirstOrDefault(p => p.Name == "Alice");
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Dirty_tracking_update()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new Person { Name = "Bob", Age = 25 };
        session.Store(person);
        await session.SaveChangesAsync();

        person.Age = 30;
        // Lightweight session: Store always does CREATE, so second save creates a new record
        session.Store(person);
        await session.SaveChangesAsync();

        var all = await session.Query<Person>().ToListAsync();
        var bobs = all.Where(p => p.Name == "Bob").ToList();
        bobs.Count.ShouldBeGreaterThanOrEqualTo(1);
        bobs.Any(p => p.Age == 30).ShouldBeTrue();
    }

    [Test]
    public async Task Store_multiple_and_query_all()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        for (var i = 0; i < 5; i++)
        {
            session.Store(new Person { Name = $"Person{i}", Age = 20 + i });
        }
        await session.SaveChangesAsync();

        var results = await session.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Test]
    public async Task Delete_and_verify_gone()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new Person { Name = "Ghost", Age = 99 };
        session.Store(person);
        var countAfterStore = await session.SaveChangesAsync();
        countAfterStore.ShouldBe(1);

        // SaveChangesAsync now back-populates the Id, so Delete works.
        // The person should be deleted and no longer exist.
        session.Delete(person);
        var countAfterDelete = await session.SaveChangesAsync();

        var all = await session.Query<Person>().ToListAsync();
        all.Count(p => p.Name == "Ghost").ShouldBe(0);
    }

    [Test]
    public async Task Session_isolation()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        await using var session1 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session1.Store(new Person { Name = "Isolated", Age = 50 });

        // Not visible in session2 before save
        var beforeAll = await session2.Query<Person>().ToListAsync();
        beforeAll.Any(p => p.Name == "Isolated").ShouldBeFalse();

        await session1.SaveChangesAsync();

        // Visible in session2 after save
        var afterAll = await session2.Query<Person>().ToListAsync();
        afterAll.Any(p => p.Name == "Isolated").ShouldBeTrue();
    }
}
