namespace Dali.Tests;

public class BatchedQueryTests
{
    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task<IDocumentStore> CreateStoreAsync()
        => await TestHarness.CreateStoreAsync();

    private static async Task SeedPeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        session.Store(new Person { Name = "Diana", Age = 22, Email = "diana@test.com" });
        await session.SaveChangesAsync();
    }

    // ─── Tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Batch_query_returns_results_for_single_compiled_query()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });

        await batch.Execute();

        var result = await alice;
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Alice");
        result.Age.ShouldBe(30);
    }

    [Test]
    public async Task Batch_query_returns_results_for_multiple_compiled_queries()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var olderThan25 = batch.Query(new ListPeopleOlderThan { MinAge = 25 });
        var bob = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        await batch.Execute();

        // Single-result query
        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // List query
        var olderList = (await olderThan25).ToList();
        olderList.Count.ShouldBe(2); // Alice(30) and Charlie(35)
        olderList.ShouldAllBe(p => p.Age > 25);

        // Another single-result
        var bobResult = await bob;
        bobResult.ShouldNotBeNull();
        bobResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_with_different_parameter_values()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var charlie = batch.Query(new FindPersonByFirstName { FirstName = "Charlie" });

        await batch.Execute();

        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        var charlieResult = await charlie;
        charlieResult.ShouldNotBeNull();
        charlieResult.Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task Batch_query_empty_returns_empty()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var batch = session.CreateBatchQuery();

        // Should not throw — no queries to execute
        await batch.Execute();
    }

    [Test]
    public async Task Batch_query_multiple_execute_calls()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();

        // First batch: Alice
        var alice = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        await batch.Execute();

        var aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // Second batch: Bob (plus Alice again, since items are additive)
        var bob = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });
        await batch.Execute();

        // Alice should still have her result (already resolved)
        aliceResult = await alice;
        aliceResult.ShouldNotBeNull();
        aliceResult.Name.ShouldBe("Alice");

        // Bob should now have his result
        var bobResult = await bob;
        bobResult.ShouldNotBeNull();
        bobResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_futures_preserve_order()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();

        // Add queries in a specific order
        var first = batch.Query(new FindPersonByFirstName { FirstName = "Charlie" });
        var second = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var third = batch.Query(new FindPersonByFirstName { FirstName = "Bob" });

        await batch.Execute();

        // Futures should preserve the order they were added
        var firstResult = await first;
        firstResult.ShouldNotBeNull();
        firstResult.Name.ShouldBe("Charlie");

        var secondResult = await second;
        secondResult.ShouldNotBeNull();
        secondResult.Name.ShouldBe("Alice");

        var thirdResult = await third;
        thirdResult.ShouldNotBeNull();
        thirdResult.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Batch_query_mixed_single_and_list_queries()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var single = batch.Query(new FindPersonByFirstName { FirstName = "Alice" });
        var list = batch.Query(new ListPeopleOlderThan { MinAge = 20 });

        await batch.Execute();

        // Single result
        var singleResult = await single;
        singleResult.ShouldNotBeNull();
        singleResult.Name.ShouldBe("Alice");

        // List result
        var listResult = (await list).ToList();
        listResult.Count.ShouldBe(4);
        listResult.ShouldAllBe(p => p.Age > 20);
    }

    [Test]
    public async Task Batch_query_with_skip_take()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        // Seed 5 people with ordered names
        session.Store(new Person { Name = "Eve", Age = 28 });
        session.Store(new Person { Name = "Frank", Age = 32 });
        session.Store(new Person { Name = "Grace", Age = 27 });
        session.Store(new Person { Name = "Hank", Age = 35 });
        session.Store(new Person { Name = "Ivy", Age = 29 });
        await session.SaveChangesAsync();

        var batch = session.CreateBatchQuery();

        // Query with Skip/Take and a simple list query together
        var paged = batch.Query(new PagedPeopleQuery { Skip = 1, Limit = 2 });
        var all = batch.Query(new ListPeopleOlderThan { MinAge = 20 });

        await batch.Execute();

        // Paged results: skip 1, take 2 → Frank, Grace
        var pagedList = (await paged).ToList();
        pagedList.Count.ShouldBe(2);
        pagedList[0].Name.ShouldBe("Frank");
        pagedList[1].Name.ShouldBe("Grace");

        // All results
        var allList = (await all).ToList();
        allList.Count.ShouldBe(5);
    }

    [Test]
    public async Task Batch_query_string_contains()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await SeedPeople(session);

        var batch = session.CreateBatchQuery();
        var containsAli = batch.Query(new ListPeopleByNameContains { Search = "Ali" });
        var containsChar = batch.Query(new ListPeopleByNameContains { Search = "Char" });

        await batch.Execute();

        var aliResults = (await containsAli).ToList();
        aliResults.Count.ShouldBe(1);
        aliResults[0].Name.ShouldBe("Alice");

        var charResults = (await containsChar).ToList();
        charResults.Count.ShouldBe(1);
        charResults[0].Name.ShouldBe("Charlie");
    }
}
