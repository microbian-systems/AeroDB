using TUnit.Core;

namespace Dali.Tests;

public class CompiledQueryTests
{
    // ─── Helpers ─────────────────────────────────────────────────────

    private static async Task<IDocumentStore> CreateStoreAsync()
    {
        var store = TestHarness.CreateStoreAsync();
        return await store;
    }

    private static async Task SeedPeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        session.Store(new Person { Name = "Diana", Age = 22, Email = "diana@test.com" });
        await session.SaveChangesAsync();
    }

    // ─── CompileQuery does not require store init ───────────────────

    [Test]
    public async Task CompileQuery_does_not_require_store_init()
    {
        // Use Documents.For WITHOUT calling InitializeAsync — compilation is purely local
        var store = Documents.For(o =>
        {
            // No client factory needed — won't be used
        });

        // This should not throw even though the store is uninitialized
        var compiled = store.CompileQuery<Person>(q =>
            q.Where(p => p.Age > 25).OrderBy(p => p.Name));

        compiled.ShouldNotBeNull();
        compiled.QueryResult.ShouldNotBeNull();
        compiled.QueryResult.Where.Count.ShouldBeGreaterThan(0);
        compiled.QueryResult.OrderBy.Count.ShouldBeGreaterThan(0);
    }

    // ─── Basic compiled query execution ─────────────────────────────

    [Test]
    public async Task Compiled_where_returns_filtered_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.Where(p => p.Age > 25));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(2); // Alice(30), Charlie(35)
        results.ShouldAllBe(p => p.Age > 25);
    }

    [Test]
    public async Task Compiled_orderBy_returns_ordered_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.OrderBy(p => p.Age));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(4);
        results[0].Age.ShouldBe(22); // Diana
        results[1].Age.ShouldBe(25); // Bob
        results[2].Age.ShouldBe(30); // Alice
        results[3].Age.ShouldBe(35); // Charlie
    }

    [Test]
    public async Task Compiled_orderByDescending_returns_ordered_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.OrderByDescending(p => p.Age));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(4);
        results[0].Age.ShouldBe(35); // Charlie
        results[^1].Age.ShouldBe(22); // Diana
    }

    [Test]
    public async Task Compiled_where_and_orderBy_work_together()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q =>
            q.Where(p => p.Age >= 25).OrderBy(p => p.Name));

        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(3); // Alice(30), Bob(25), Charlie(35)
    }

    // ─── Skip / Take ────────────────────────────────────────────────

    [Test]
    public async Task Compiled_skip_take_works()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "A", Age = 10 });
        session.Store(new Person { Name = "B", Age = 20 });
        session.Store(new Person { Name = "C", Age = 30 });
        await session.SaveChangesAsync();

        var compiled = store.CompileQuery<Person>(q => q.OrderBy(p => p.Name).Skip(1).Take(1));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("B");
    }

    // ─── FirstOrDefault ─────────────────────────────────────────────

    [Test]
    public async Task Compiled_firstOrDefault_returns_first()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.OrderBy(p => p.Age));
        var result = await session.QueryFirstOrDefaultAsync(compiled);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Diana"); // youngest
        result.Age.ShouldBe(22);
    }

    [Test]
    public async Task Compiled_firstOrDefault_empty_returns_null()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var compiled = store.CompileQuery<Person>(q => q.Where(p => p.Name == "NonExistent"));
        var result = await session.QueryFirstOrDefaultAsync(compiled);

        result.ShouldBeNull();
    }

    [Test]
    public async Task Compiled_firstOrDefault_with_filter_returns_matching()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.Where(p => p.Name == "Alice"));
        var result = await session.QueryFirstOrDefaultAsync(compiled);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Alice");
    }

    // ─── Reuse produces same results ───────────────────────────────

    [Test]
    public async Task Compiled_reuse_produces_consistent_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q => q.Where(p => p.Age > 25));

        // Execute twice
        var first = await session.QueryAsync(compiled);
        var second = await session.QueryAsync(compiled);

        first.Count.ShouldBe(2);
        second.Count.ShouldBe(2);
        first[0].Name.ShouldBe(second[0].Name);
        first[1].Name.ShouldBe(second[1].Name);
    }

    // ─── Multi-condition ────────────────────────────────────────────

    [Test]
    public async Task Compiled_multi_condition_where()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // Age between 25 and 32 inclusive
        var compiled = store.CompileQuery<Person>(q =>
            q.Where(p => p.Age >= 25 && p.Age <= 32));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(2); // Alice(30), Bob(25)
        results.ShouldAllBe(p => p.Age >= 25 && p.Age <= 32);
    }

    // ─── String operations ──────────────────────────────────────────

    [Test]
    public async Task Compiled_string_contains()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var compiled = store.CompileQuery<Person>(q =>
            q.Where(p => p.Email.Contains("@test")));
        var results = await session.QueryAsync(compiled);

        results.Count.ShouldBe(4);
    }

    // ─── SurrealQueryResult cloning is thread-safe ──────────────────

    [Test]
    public async Task Compiled_query_result_clone_is_independent()
    {
        var store = Documents.For(o => { });
        var compiled = store.CompileQuery<Person>(q => q.Where(p => p.Age > 25));

        var original = compiled.QueryResult;
        var clone = original.Clone();

        // Mutate the clone
        clone.Limit = 5;
        clone.Where.Add("extra = true");

        original.Limit.ShouldBeNull();
        original.Where.Count.ShouldBe(1);
        clone.Limit.ShouldBe(5);
        clone.Where.Count.ShouldBe(2);
    }
}
