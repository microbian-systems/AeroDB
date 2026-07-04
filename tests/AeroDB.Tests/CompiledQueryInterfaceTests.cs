using System.Linq.Expressions;
using AeroDB;

namespace AeroDB.Tests;

// ── Compiled query implementations used by tests ──────────────────────

/// <summary>
/// Finds a person by exact name match. Test query for <see cref="ICompiledQuery{TDoc,TOut}"/>.
/// </summary>
public class FindPersonByFirstName : ICompiledQuery<Person, Person>
{
    public string FirstName { get; set; } = "";
    public Expression<Func<ISurrealDbQueryable<Person>, Person>> QueryIs()
        => q => q.Where(x => x.Name == FirstName).FirstOrDefault()!;
}

/// <summary>
/// Lists people older than a given age.
/// </summary>
public class ListPeopleOlderThan : ICompiledListQuery<Person>
{
    public int MinAge { get; set; }
    public Expression<Func<ISurrealDbQueryable<Person>, IEnumerable<Person>>> QueryIs()
        => q => q.Where(x => x.Age > MinAge);
}

/// <summary>
/// Lists people older than a given age, ordered by name.
/// </summary>
public class ListPeopleOlderThanOrdered : ICompiledListQuery<Person>
{
    public int MinAge { get; set; }
    public Expression<Func<ISurrealDbQueryable<Person>, IEnumerable<Person>>> QueryIs()
        => q => q.Where(x => x.Age > MinAge).OrderBy(x => x.Name);
}

/// <summary>
/// Finds a person by exact name match, shorthand interface.
/// </summary>
public class FindPersonByFirstNameShorthand : ICompiledQuery<Person>
{
    public string FirstName { get; set; } = "";
    public Expression<Func<ISurrealDbQueryable<Person>, Person>> QueryIs()
        => q => q.Where(x => x.Name == FirstName).FirstOrDefault()!;
}

/// <summary>
/// Paginated people query — uses Skip/Take from properties.
/// The <see cref="Skip"/> and <see cref="Limit"/> property values are substituted
/// into SurrealQL START/LIMIT clauses at execution time.
/// </summary>
public class PagedPeopleQuery : ICompiledListQuery<Person>
{
    public int Skip { get; set; }
    public int Limit { get; set; } = 10;
    public Expression<Func<ISurrealDbQueryable<Person>, IEnumerable<Person>>> QueryIs()
        => q => q.OrderBy(x => x.Name).Skip(Skip).Take(Limit);
}

/// <summary>
/// Lists people filtered by name using string Contains.
/// </summary>
public class ListPeopleByNameContains : ICompiledListQuery<Person>
{
    public string Search { get; set; } = "";
    public Expression<Func<ISurrealDbQueryable<Person>, IEnumerable<Person>>> QueryIs()
        => q => q.Where(x => x.Name.Contains(Search));
}

// ── Tests ──────────────────────────────────────────────────────────────

public class CompiledQueryInterfaceTests
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
    public async Task Compiled_interface_returns_filtered_result()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new FindPersonByFirstName { FirstName = "Alice" };
        var result = await session.QueryAsync(query);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Compiled_interface_list_query_with_where_returns_correct_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new ListPeopleOlderThan { MinAge = 25 };
        var results = await session.QueryAsync(query);

        results.ShouldNotBeNull();
        var list = results.ToList();
        list.Count.ShouldBe(2); // Alice(30) and Charlie(35)
        list.ShouldAllBe(p => p.Age > 25);
    }

    [Test]
    public async Task Compiled_interface_list_with_order()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new ListPeopleOlderThanOrdered { MinAge = 20 };
        var results = await session.QueryAsync(query);

        results.ShouldNotBeNull();
        var list = results.ToList();
        list.Count.ShouldBe(4);
        list[0].Name.ShouldBe("Alice");
        list[1].Name.ShouldBe("Bob");
        list[2].Name.ShouldBe("Charlie");
        list[3].Name.ShouldBe("Diana");
    }

    [Test]
    public async Task Compiled_interface_single_result_query()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new FindPersonByFirstNameShorthand { FirstName = "Bob" };
        var result = await session.QueryAsync(query);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Bob");
        result.Age.ShouldBe(25);
    }

    [Test]
    public async Task Compiled_interface_empty_results_returns_empty_list()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new ListPeopleOlderThan { MinAge = 100 };
        var results = await session.QueryAsync(query);

        results.ShouldNotBeNull();
        results.Count().ShouldBe(0);
    }

    [Test]
    public async Task Compiled_interface_different_parameters_produce_different_results()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var aliceQuery = new FindPersonByFirstName { FirstName = "Alice" };
        var bobQuery = new FindPersonByFirstName { FirstName = "Bob" };

        var alice = await session.QueryAsync(aliceQuery);
        var bob = await session.QueryAsync(bobQuery);

        alice.ShouldNotBeNull();
        alice.Name.ShouldBe("Alice");

        bob.ShouldNotBeNull();
        bob.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Compiled_interface_single_result_not_found_returns_default()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new FindPersonByFirstName { FirstName = "NonExistent" };
        var result = await session.QueryAsync(query);

        // For a single-result compiled query, default(Person) is null (Person is a reference type)
        // But the QueryAsync signature returns TOut which is Person for this query.
        // The planner returns default! for the single result.
        // Person is a class, so default is null.
        result.ShouldBeNull();
    }

    [Test]
    public async Task Compiled_interface_multiple_instances_work_concurrently()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var queryA = new FindPersonByFirstName { FirstName = "Alice" };
        var queryB = new FindPersonByFirstName { FirstName = "Bob" };

        // Execute concurrently to verify thread safety
        var taskA = session.QueryAsync(queryA);
        var taskB = session.QueryAsync(queryB);

        await Task.WhenAll(taskA, taskB);

        taskA.Result.ShouldNotBeNull();
        taskA.Result.Name.ShouldBe("Alice");

        taskB.Result.ShouldNotBeNull();
        taskB.Result.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Compiled_interface_string_contains()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var query = new ListPeopleByNameContains { Search = "Ali" };
        var results = await session.QueryAsync(query);

        results.ShouldNotBeNull();
        var list = results.ToList();
        list.Count.ShouldBe(1);
        list[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Compiled_interface_skip_take_pagination()
    {
        await using var store = await CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Seed 5 people with ordered names
        session.Store(new Person { Name = "Eve", Age = 28 });
        session.Store(new Person { Name = "Frank", Age = 32 });
        session.Store(new Person { Name = "Grace", Age = 27 });
        session.Store(new Person { Name = "Hank", Age = 35 });
        session.Store(new Person { Name = "Ivy", Age = 29 });
        await session.SaveChangesAsync();

        // Page 2 with page size 2 → skip 2, take 2
        var query = new PagedPeopleQuery { Skip = 2, Limit = 2 };
        var results = await session.QueryAsync(query);

        results.ShouldNotBeNull();
        var list = results.ToList();
        list.Count.ShouldBe(2);
        list[0].Name.ShouldBe("Grace");
        list[1].Name.ShouldBe("Hank");
    }
}
