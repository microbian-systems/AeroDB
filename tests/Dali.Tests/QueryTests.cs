using TUnit.Core;

namespace Dali.Tests;

public class QueryTests
{
    [Test]
    public async Task Query_all_returns_results()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Take_limits_results()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        session.Store(new Person { Name = "A", Age = 10 });
        session.Store(new Person { Name = "B", Age = 20 });
        session.Store(new Person { Name = "C", Age = 30 });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>().Take(2).ToListAsync();
        results.Count.ShouldBe(2);
    }
}
