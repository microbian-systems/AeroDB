using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

public class LoadManyTests
{
    [Test]
    public async Task LoadMany_with_two_ids_returns_both()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice", Age = 30 };
        var bob = new Person { Name = "Bob", Age = 25 };
        session.Store(alice);
        session.Store(bob);
        await session.SaveChangesAsync();

        alice.Id.ShouldNotBeNull();
        bob.Id.ShouldNotBeNull();

        var ids = new[] { alice.Id!.DeserializeId<string>(), bob.Id!.DeserializeId<string>() };
        var results = await session.LoadManyAsync<Person>(ids);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.Any(p => p.Name == "Alice").ShouldBeTrue();
        results.Any(p => p.Name == "Bob").ShouldBeTrue();
    }

    [Test]
    public async Task LoadMany_with_empty_list_returns_empty()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var results = await session.LoadManyAsync<Person>(Array.Empty<string>());

        results.ShouldNotBeNull();
        results.Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadMany_with_one_valid_and_one_missing_id_returns_only_valid()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice", Age = 30 };
        session.Store(alice);
        await session.SaveChangesAsync();

        alice.Id.ShouldNotBeNull();

        var validId = alice.Id!.DeserializeId<string>();
        var ids = new[] { validId, "nonexistent_id" };
        var results = await session.LoadManyAsync<Person>(ids);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task LoadMany_with_RecordId_overload()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var alice = new Person { Name = "Alice", Age = 30 };
        var bob = new Person { Name = "Bob", Age = 25 };
        session.Store(alice);
        session.Store(bob);
        await session.SaveChangesAsync();

        alice.Id.ShouldNotBeNull();
        bob.Id.ShouldNotBeNull();

        var recordIds = new[] { alice.Id!, bob.Id! };
        var results = await session.LoadManyAsync<Person>(recordIds);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.Any(p => p.Name == "Alice").ShouldBeTrue();
        results.Any(p => p.Name == "Bob").ShouldBeTrue();
    }

    [Test]
    public async Task LoadMany_with_numeric_ids_overload()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        // Store entities with specific numeric IDs via raw query
        await session.ExecuteSqlAsync("CREATE person:100 CONTENT { Name: 'Alice', Age: 30 };");
        await session.ExecuteSqlAsync("CREATE person:200 CONTENT { Name: 'Bob', Age: 25 };");

        var results = await session.LoadManyAsync<Person>("person", new long[] { 100, 200 });

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.Any(p => p.Name == "Alice").ShouldBeTrue();
        results.Any(p => p.Name == "Bob").ShouldBeTrue();
    }
}
