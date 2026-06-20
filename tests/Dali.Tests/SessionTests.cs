using TUnit.Core;

namespace Dali.Tests;

public class SessionTests
{
    [Test]
    public async Task Store_entity_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

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
        await using var session = await store.LightweightSessionAsync();

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

        await using var ws = await store.LightweightSessionAsync();
        ws.Store(new Person { Name = "Zoe", Age = 40 });
        await ws.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var results = await qs.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
}
