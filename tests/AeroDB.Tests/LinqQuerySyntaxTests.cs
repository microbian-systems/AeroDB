using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Exercises AeroDB's support for C# LINQ query comprehension syntax —
/// the <c>from x in source where cond select x</c> form.
///
/// Every test is self-contained: creates its own store, seeds its own data,
/// queries using query comprehension, and validates results with Shouldly.
///
/// Query comprehension is compiled to the same expression trees as
/// fluent method-chaining by the C# compiler, so any SurrealQueryProvider
/// limitation (e.g. unsupported string methods, computed projections) applies
/// equally to both forms. Where a construct isn't supported, the test verifies
/// the expected behaviour (exception or SQL output) and documents why.
/// </summary>
public class LinqQuerySyntaxTests
{
    // ─── Seed helpers ───────────────────────────────────────────────

    private static async Task SeedPeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        session.Store(new Person { Name = "Diana", Age = 22, Email = "diana@test.com" });
        await session.SaveChangesAsync();
    }

    private static async Task SeedThreePeople(IDocumentSession session)
    {
        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Person { Name = "Charlie", Age = 35, Email = "charlie@test.com" });
        await session.SaveChangesAsync();
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. Basic identity projection
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_BasicIdentityProjection_Works()
    {
        // from p in source select p  —  the simplest query comprehension.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             select p).ToListAsync();

        results.Count.ShouldBe(4);
        results.ShouldAllBe(p => p.Name != null);
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. Where with simple equality condition
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_SimpleCondition_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name == "Alice"
                             select p).ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
        results[0].Age.ShouldBe(30);
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. Compound condition (AND, &&)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_CompoundConditions_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name == "Alice" && p.Age > 20
                             select p).ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. String.StartsWith
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_StringStartsWith_Works()
    {
        // Person only has Name (not Firstname), so we use p.Name here.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name.StartsWith("A")
                             select p).ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. String.Contains
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_StringContains_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name.Contains("li")
                             select p).ToListAsync();

        results.Count.ShouldBe(2);
        results.Select(p => p.Name).ShouldContain("Alice");
        results.Select(p => p.Name).ShouldContain("Charlie");
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. String.EndsWith
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_StringEndsWith_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name.EndsWith("ce")
                             select p).ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 7. OrderBy ascending (single key)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_OrderBy_Ascending_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             orderby p.Name
                             select p).ToListAsync();

        results.Count.ShouldBe(4);
        results[0].Name.ShouldBe("Alice");
        results[1].Name.ShouldBe("Bob");
        results[2].Name.ShouldBe("Charlie");
        results[3].Name.ShouldBe("Diana");
    }

    // ═════════════════════════════════════════════════════════════════
    // 8. OrderBy descending
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_OrderBy_Descending_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             orderby p.Name descending
                             select p).ToListAsync();

        results.Count.ShouldBe(4);
        results[0].Name.ShouldBe("Diana");
        results[1].Name.ShouldBe("Charlie");
        results[2].Name.ShouldBe("Bob");
        results[3].Name.ShouldBe("Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 9. Where + OrderBy combined
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_And_OrderBy_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Age > 25
                             orderby p.Name
                             select p).ToListAsync();

        results.Count.ShouldBe(2); // Alice(30), Charlie(35)
        results[0].Name.ShouldBe("Alice");
        results[1].Name.ShouldBe("Charlie");
        results.ShouldAllBe(p => p.Age > 25);
    }

    // ═════════════════════════════════════════════════════════════════
    // 10. Anonymous type projection (new { ... })
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_AnonymousProjection_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // Query comprehension with a NewExpression projection.
        // The visitor extracts each member argument and generates
        // SELECT Name, Age FROM person WHERE ...
        var results = await (from p in session.Query<Person>()
                             where p.Age > 20
                             select new { p.Name, p.Age }).ToListAsync();

        results.Count.ShouldBeGreaterThanOrEqualTo(4);
        results.ShouldAllBe(r => r.Age > 20);
        results.ShouldContain(r => r.Name == "Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 11. Multiple where clauses (both combined with AND)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_MultipleWhere_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // Multiple sequential WHERE clauses should be ANDed together.
        var results = await (from p in session.Query<Person>()
                             where p.Age > 20
                             where p.Age < 50
                             select p).ToListAsync();

        results.Count.ShouldBe(4);
        results.ShouldAllBe(p => p.Age > 20 && p.Age < 50);
    }

    // ═════════════════════════════════════════════════════════════════
    // 12. Query syntax + method syntax (Take)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_WithTake_ViaMethodSyntax_MixesWell()
    {
        // Query comprehension and fluent method calls compose naturally:
        // the parens wrap the comprehension, then .Take(n) is applied.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Age > 20
                             orderby p.Name
                             select p).Take(2).ToListAsync();

        results.Count.ShouldBe(2);
        // Order is guaranteed by the ORDER BY clause
        results[0].Name.ShouldBe("Alice");
        results[1].Name.ShouldBe("Bob");
    }

    // ═════════════════════════════════════════════════════════════════
    // 13. Empty result — returns empty list (not null, not error)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_EmptyResult_ReturnsEmptyList()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // A WHERE clause that matches no records should return a 0-length
        // list, not null, and not throw.
        var results = await (from p in session.Query<Person>()
                             where p.Age > 200
                             select p).ToListAsync();

        results.ShouldNotBeNull();
        results.Count.ShouldBe(0);
    }

    // ═════════════════════════════════════════════════════════════════
    // 14. Standalone select with no WHERE returns all
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_StandaloneSelect_NoWhere_ReturnsAll()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // A query with only SELECT (no WHERE, no ORDER BY) returns every row.
        var results = await (from p in session.Query<Person>()
                             select new { p.Name }).ToListAsync();

        results.Count.ShouldBe(4);
        results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Name));
    }

    // ═════════════════════════════════════════════════════════════════
    // 15. Compound OR condition (||)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_OrCondition_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // WHERE Name = "Alice" OR Age = 25
        var results = await (from p in session.Query<Person>()
                             where p.Name == "Alice" || p.Age == 25
                             select p).ToListAsync();

        results.Count.ShouldBe(2);
        results.ShouldContain(r => r.Name == "Alice");
        results.ShouldContain(r => r.Name == "Bob");
    }

    // ═════════════════════════════════════════════════════════════════
    // 16. Multiple OrderBy (descending primary, ascending secondary)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_MultipleOrderBy_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        // orderby p.Age descending, p.Name
        // → ThenByDescending first, then OrderBy (ascending by default)
        // The visitor handles both OrderByDescending and ThenBy.
        var results = await (from p in session.Query<Person>()
                             orderby p.Age descending, p.Name
                             select p).ToListAsync();

        results.Count.ShouldBe(4);
        results[0].Age.ShouldBe(35);  // Charlie (oldest)
        results[1].Age.ShouldBe(30);  // Alice
        results[2].Age.ShouldBe(25);  // Bob
        results[3].Age.ShouldBe(22);  // Diana (youngest)
    }

    // ═════════════════════════════════════════════════════════════════
    // 17. Not-equal inequality (!=)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_NotEqual_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Name != "Alice"
                             select p).ToListAsync();

        results.Count.ShouldBe(3);
        results.ShouldAllBe(p => p.Name != "Alice");
    }

    // ═════════════════════════════════════════════════════════════════
    // 18. Greater-than-or-equal (>=)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_GreaterThanOrEqual_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Age >= 30
                             select p).ToListAsync();

        results.Count.ShouldBe(2);
        results.ShouldAllBe(p => p.Age >= 30);
    }

    // ═════════════════════════════════════════════════════════════════
    // 19. Less-than (<)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_LessThan_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Age < 30
                             select p).ToListAsync();

        results.Count.ShouldBe(2); // Bob(25), Diana(22)
        results.ShouldAllBe(p => p.Age < 30);
    }

    // ═════════════════════════════════════════════════════════════════
    // 20. Select with full identity + Skip
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_WithSkip_ViaMethodSyntax_MixesWell()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             orderby p.Name
                             select p).Skip(2).ToListAsync();

        results.Count.ShouldBe(2);
        results[0].Name.ShouldBe("Charlie");
        results[1].Name.ShouldBe("Diana");
    }

    // ═════════════════════════════════════════════════════════════════
    // 21. ToCommand — SQL inspection without execution
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_ToCommand_ProducesSurrealQL()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Build a query using comprehension, then inspect the generated SQL
        // without executing it.  ToCommand() is a zero-round-trip operation.
        //
        // Note: we avoid `orderby` here because AeroDB defines custom Where/Select
        // extension methods on ISurrealDbQueryable<T>, but no custom OrderBy.
        // After `orderby` the static type becomes IOrderedQueryable<T> which
        // doesn't expose ToCommand(). Use a cast or avoid orderby for ToCommand.
        var query = from p in session.Query<Person>()
                    where p.Age > 25
                    select p;

        var sql = ((ISurrealDbQueryable<Person>)query).ToCommand();
        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("SELECT");
        sql.ShouldContain("FROM");
        sql.ShouldContain("person");         // table name
        sql.ShouldContain("Age");            // column in WHERE
        sql.ShouldNotContain("ERROR");
    }

    // ═════════════════════════════════════════════════════════════════
    // 22. ToCommand with anonymous projection
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_ToCommand_WithAnonymousProjection()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Use `where` + `select` (no `orderby`) so the custom Select extension
        // on ISurrealDbQueryable<T> preserves the ToCommand() method.
        var query = from p in session.Query<Person>()
                    where p.Age > 20
                    select new { p.Name, p.Age };

        var sql = query.ToCommand();
        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("SELECT");
        sql.ShouldContain("Name");
        sql.ShouldContain("Age");
    }

    // ═════════════════════════════════════════════════════════════════
    // 23. Where with NOT by using inverted condition (age not > 25)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_LogicalNot_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where !(p.Age > 30)
                             select p).ToListAsync();

        // Alice(30), Bob(25), Diana(22) → NOT (Age > 30) => Age <= 30
        results.Count.ShouldBe(3);
        results.ShouldAllBe(p => p.Age <= 30);
    }

    // ═════════════════════════════════════════════════════════════════
    // 24. Query with both Skip and Take
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_WithSkipAndTake_MixesWell()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             orderby p.Name
                             select p).Skip(1).Take(2).ToListAsync();

        results.Count.ShouldBe(2);
        results[0].Name.ShouldBe("Bob");
        results[1].Name.ShouldBe("Charlie");
    }

    // ═════════════════════════════════════════════════════════════════
    // 25. Where with combined range (Age > 20 AND Age < 36)
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Where_RangeCondition_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await (from p in session.Query<Person>()
                             where p.Age > 22 && p.Age < 35
                             select p).ToListAsync();

        results.Count.ShouldBe(2); // Alice(30), Bob(25)
        results.ShouldAllBe(p => p.Age > 22 && p.Age < 35);
    }

    // ═════════════════════════════════════════════════════════════════
    // LET clause — known limitation
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Let_ToCommand_InspectsSQL()
    {
        // The C# `let` keyword translates the query comprehension into a
        // Select call that creates an anonymous type capturing the original
        // range variable plus the computed expression:
        //
        //   from p in source
        //   let upper = p.Name.ToUpper()
        //   where upper.StartsWith("A")
        //   select p
        //
        // → source.Select(p => new { p, upper = p.Name.ToUpper() })
        //         .Where(x => x.upper.StartsWith("A"))
        //         .Select(x => x.p)
        //
        // The SurrealExpressionVisitor handles the Where just fine
        // (string::starts_with is supported), but the inner Select's
        // NewExpression arguments are non-MemberExpression nodes:
        //   - ParameterExpression p → ProjMember returns "*"
        //   - MethodCallExpression p.Name.ToUpper() → ProjMember returns "*"
        //
        // This produces _projection = "*, *", which is invalid SurrealQL.
        // Additionally, the Where clause references `upper` — a column that
        // doesn't exist on the person table.
        //
        // This test documents the limitation: `let` does NOT produce a
        // correct query with the current expression visitor.

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var query = from p in session.Query<Person>()
                    let upper = p.Name.ToUpper()
                    where upper.StartsWith("A")
                    select p;

        var sql = query.ToCommand();
        // The SQL is generated without throwing, but the projection is
        // invalid due to the ProjMember fallback.
        sql.ShouldNotBeNullOrEmpty();
        // The inner Select's NewExpression causes "*, *" in the projection
        // because both arguments are non-MemberExpression nodes.
        // Note: the exact output depends on whether the visitor collapses
        // duplicate "*" entries; at minimum it will have "SELECT" and "FROM".
        sql.ShouldContain("SELECT");
        sql.ShouldContain("FROM");
        // The Where condition references a non-existent column `upper`.
        sql.ShouldContain("starts_with");
    }

    // ═════════════════════════════════════════════════════════════════
    // Computed value in Select — known limitation
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Select_WithComputedValue_ToCommand_InspectsSQL()
    {
        // The SurrealExpressionVisitor's Select handler uses ProjMember()
        // on each argument of a NewExpression (anonymous type) to build the
        // projection string. ProjMember returns "*" for any expression that
        // is not a simple MemberExpression.
        //
        // For: select new { p.Name, EstimatedYear = 2026 - p.Age }
        //   - p.Name            → MemberExpression → "Name"        ✓
        //   - 2026 - p.Age      → BinaryExpression → "*"           ✗
        //
        // Resulting projection: "Name, *" — invalid SurrealQL.

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var query = from p in session.Query<Person>()
                    select new { p.Name, EstimatedYear = 2026 - p.Age };

        var sql = query.ToCommand();
        sql.ShouldNotBeNullOrEmpty();
        sql.ShouldContain("SELECT");
        sql.ShouldContain("FROM");
        // The computed expression falls back to "*" in the projection.
        // This test documents the current limitation — computed/anonymous
        // expressions in Select projections require visitor enhancements.
        sql.ShouldContain("Name");
    }

    // ═════════════════════════════════════════════════════════════════
    // BONUS: RawQueryAsync — interop with session-level queries
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_RawQueryAsync_Works()
    {
        // While not query-comprehension syntax, this demonstrates that
        // session.RawQueryAsync<T>() can be used alongside or as an
        // alternative to LINQ queries for ad-hoc SurrealQL.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE age > 18");

        results.ShouldNotBeNull();
        results.Count.ShouldBe(4);
        results.ShouldAllBe(p => p.Age > 18);
    }

    // ═════════════════════════════════════════════════════════════════
    // BONUS: Count on query syntax result
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_Count_ViaMethodSyntax_Works()
    {
        // Query comprehension yields IQueryable<Person>, which implements
        // ISurrealDbQueryable<Person>. The CountAsync() method defined on
        // the interface can be called directly.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var count = await (from p in session.Query<Person>()
                           where p.Age > 25
                           select p).CountAsync();

        count.ShouldBe(2); // Alice(30), Charlie(35)
    }

    // ═════════════════════════════════════════════════════════════════
    // BONUS: FirstOrDefault on query syntax result
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_FirstOrDefault_ViaMethodSyntax_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var person = await (from p in session.Query<Person>()
                            where p.Name == "Alice"
                            select p).FirstOrDefaultAsync();

        person.ShouldNotBeNull();
        person!.Name.ShouldBe("Alice");
        person.Age.ShouldBe(30);
    }

    // ═════════════════════════════════════════════════════════════════
    // BONUS: AnyAsync on query syntax result
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_AnyAsync_ViaMethodSyntax_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var any = await (from p in session.Query<Person>()
                         where p.Name == "Alice"
                         select p).AnyAsync();

        any.ShouldBeTrue();
    }

    // ═════════════════════════════════════════════════════════════════
    // BONUS: SingleOrDefault on query syntax result
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public async Task QuerySyntax_SingleOrDefault_ViaMethodSyntax_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await SeedPeople(session);

        var person = await (from p in session.Query<Person>()
                            where p.Name == "Bob"
                            select p).SingleOrDefaultAsync();

        person.ShouldNotBeNull();
        person!.Name.ShouldBe("Bob");
        person.Age.ShouldBe(25);
    }
}
