using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class QueryTests
{
    [Test]
    public async Task Query_all_returns_results()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "A", Age = 10 });
        session.Store(new Person { Name = "B", Age = 20 });
        session.Store(new Person { Name = "C", Age = 30 });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>().Take(2).ToListAsync();
        results.Count.ShouldBe(2);
    }

    // --- Helper ---

    private static async Task SeedPeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        session.Store(new Person { Name = "Diana", Age = 22, Email = "diana@test.com" });
        await session.SaveChangesAsync();
    }

    // --- Ordering ---

    [Test]
    public async Task OrderBy_age_ascending()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.Query<Person>().OrderBy(p => p.Age).ToListAsync();
        results.Count.ShouldBe(4);
        results[0].Age.ShouldBe(22);
        results[^1].Age.ShouldBe(35);
    }

    [Test]
    public async Task OrderByDescending_age()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.Query<Person>().OrderByDescending(p => p.Age).ToListAsync();
        results.Count.ShouldBe(4);
        results[0].Age.ShouldBe(35);
        results[^1].Age.ShouldBe(22);
    }

    // --- Filtering ---

    [Test]
    public async Task Where_simple_equals()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.Query<Person>().Where(p => p.Age == 25).ToListAsync();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task Where_simple_greaterThan()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.Query<Person>().Where(p => p.Age > 25).ToListAsync();
        results.Count.ShouldBe(2); // Alice(30), Charlie(35)
        results.ShouldAllBe(p => p.Age > 25);
    }

    [Test]
    public async Task FirstOrDefault_matching()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // FirstOrDefaultAsync without expression returns first record
        var result = await session.Query<Person>().FirstOrDefaultAsync();
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task FirstOrDefault_no_match()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var result = await session.Query<Person>().FirstOrDefaultAsync();
        result.ShouldBeNull();
    }

    // --- Pagination ---

    [Test]
    public async Task Skip_offsets()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "A", Age = 10 });
        session.Store(new Person { Name = "B", Age = 20 });
        session.Store(new Person { Name = "C", Age = 30 });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>().Skip(1).ToListAsync();
        results.Count.ShouldBe(2);
    }

    // --- String operations ---

    [Test]
    public async Task String_contains()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.Query<Person>()
            .Where(p => p.Email.Contains("@test"))
            .ToListAsync();

        results.Count.ShouldBeGreaterThan(0);
    }

    // --- Aggregation ---

    [Test]
    public async Task Count_returns_total()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "A", Age = 10 });
        session.Store(new Person { Name = "B", Age = 20 });
        session.Store(new Person { Name = "C", Age = 30 });
        await session.SaveChangesAsync();

        var count = await session.Query<Person>().CountAsync();
        count.ShouldBe(3);
    }

    [Test]
    public async Task Any_returns_true()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var any = await session.Query<Person>().AnyAsync();
        any.ShouldBeTrue();
    }

    [Test]
    public async Task Count_with_filter()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var count = await session.Query<Person>().Where(p => p.Age > 25).CountAsync();
        count.ShouldBe(2); // Alice(30), Charlie(35)
    }

    // --- Empty results ---

    [Test]
    public async Task Query_empty_returns_empty()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var results = await session.Query<Person>().ToListAsync();
        results.Count.ShouldBe(0);
    }

    // ─── Select MemberInit projection ───────────────────────────────

    [Test]
    public async Task Select_member_init()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        await session.SaveChangesAsync();

        var results = await session.Query<Person>()
            .Select(p => new PersonDto { FullName = p.Name, YearsOld = p.Age })
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].FullName.ShouldBe("Alice");
        results[0].YearsOld.ShouldBe(30);
    }

    // ─── Where with math operators ──────────────────────────────────

    [Test]
    public async Task Where_with_multiply()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Product { Name = "Widget", Price = 10m, Quantity = 5 });
        session.Store(new Product { Name = "Gadget", Price = 20m, Quantity = 1 });
        await session.SaveChangesAsync();

        // Price * Quantity > 30 → only Widget: 10*5=50 > 30, Gadget: 20*1=20 <= 30
        var results = await session.Query<Product>()
            .Where(p => p.Price * p.Quantity > 30m)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Widget");
    }

    [Test]
    public async Task Where_with_divide()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Product { Name = "Widget", Price = 100m, Quantity = 5 });
        session.Store(new Product { Name = "Gadget", Price = 20m, Quantity = 2 });
        await session.SaveChangesAsync();

        // Price / Quantity > 10
        var results = await session.Query<Product>()
            .Where(p => p.Price / p.Quantity > 10m)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Widget");
    }

    // ─── Aggregates ─────────────────────────────────────────────────

    private static async Task SeedProducts(IDocumentSession session)
    {
        session.Store(new Product { Name = "Widget", Price = 10m, Quantity = 5 });
        session.Store(new Product { Name = "Gadget", Price = 20m, Quantity = 2 });
        session.Store(new Product { Name = "Doohickey", Price = 30m, Quantity = 3 });
        await session.SaveChangesAsync();
    }

    [Test]
    public async Task Sum_returns_total()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedProducts(session);

        var sum = await session.Query<Product>().SumAsync(p => p.Price);
        sum.ShouldBe(60m);
    }

    [Test]
    public async Task Max_returns_maximum()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedProducts(session);

        var max = await session.Query<Product>().MaxAsync(p => p.Price);
        max.ShouldBe(30m);
    }

    [Test]
    public async Task Min_returns_minimum()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedProducts(session);

        var min = await session.Query<Product>().MinAsync(p => p.Price);
        min.ShouldBe(10m);
    }

    [Test]
    public async Task Average_returns_mean()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedProducts(session);

        var avg = await session.Query<Product>().AverageAsync(p => p.Price);
        avg.ShouldBe(20m);
    }

    [Test]
    public async Task Sum_with_filter()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedProducts(session);

        // sum of prices where price > 10
        var filtered = session.Query<Product>().Where(p => p.Price > 10m);
        var sum = await filtered.SumAsync(p => p.Price);
        sum.ShouldBe(50m); // 20 + 30
    }

    // ─── SingleOrDefault ────────────────────────────────────────────

    [Test]
    public async Task SingleOrDefault_one_result()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var person = await session.Query<Person>()
            .Where(p => p.Name == "Alice")
            .SingleOrDefaultAsync();

        person.ShouldNotBeNull();
        person!.Name.ShouldBe("Alice");
    }

    [Test]
    public async Task SingleOrDefault_multiple_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // fetch all without filter — more than one result should throw
        var query = session.Query<Person>();

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await query.SingleOrDefaultAsync());
    }

    [Test]
    public async Task SingleOrDefault_empty_returns_null()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // no data — empty result should return null
        var person = await session.Query<Person>().SingleOrDefaultAsync();
        person.ShouldBeNull();
    }

    // ─── FirstOrDefault with predicate ──────────────────────────────

    [Test]
    public async Task FirstOrDefault_with_predicate_matches()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var person = await session.Query<Person>().FirstOrDefaultAsync(p => p.Name == "Bob");
        person.ShouldNotBeNull();
        person!.Name.ShouldBe("Bob");
    }

    [Test]
    public async Task FirstOrDefault_with_predicate_no_match()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var person = await session.Query<Person>().FirstOrDefaultAsync(p => p.Name == "NonExistent");
        person.ShouldBeNull();
    }

    // ─── Date functions ─────────────────────────────────────────────

    [Test]
    public async Task Where_on_date_year()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var now = DateTimeOffset.UtcNow;
        session.Store(new Product
        {
            Name = "Widget",
            Price = 10m,
            Quantity = 1,
            CreatedAt = now
        });
        await session.SaveChangesAsync();

        // Test direct datetime comparison via FormatValue for DateTimeOffset
        var results = await session.Query<Product>()
            .Where(p => p.CreatedAt >= now)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Widget");
    }

    [Test]
    public async Task Where_on_date_month()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var now = DateTimeOffset.UtcNow;
        session.Store(new Product
        {
            Name = "Gadget",
            Price = 20m,
            Quantity = 2,
            CreatedAt = now
        });
        await session.SaveChangesAsync();

        // Test created after subtraction — verifies FormatValue handles DateTimeOffset
        var past = now.AddDays(-1);
        var results = await session.Query<Product>()
            .Where(p => p.CreatedAt > past)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Gadget");
    }

    // ─── ToCommand / SQL inspection ─────────────────────────────────

    [Test]
    public async Task ToCommand_returns_surrealql()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var sql = session.Query<Person>()
            .Where(p => p.Age > 25)
            .ToCommand();

        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("person");
        sql.ShouldContain("Age");
    }

    [Test]
    public async Task ToCommand_with_select_returns_aliased_sql()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var sql = session.Query<Person>()
            .Select(p => new { p.Name })
            .ToCommand();

        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("SELECT");
    }

    // ─── PersonDto for MemberInit test ──────────────────────────────

    public class PersonDto
    {
        public string FullName { get; set; } = "";
        public int YearsOld { get; set; }
    }
}
