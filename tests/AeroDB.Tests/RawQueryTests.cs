using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class RawQueryTests
{
    [Test]
    public async Task RawQueryAsync_ReturnsTypedResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE age > 25");

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task RawQueryAsync_WithParameters()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE age > $minAge",
            new Dictionary<string, object?> { ["minAge"] = 25 });

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
    }

    [Test]
    public async Task RawQueryAsync_EmptyResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Store a record to create the table, then query with a non-matching filter
        writeSession.Store(new Person { Name = "Alice", Age = 30 });
        await writeSession.SaveChangesAsync();

        await using var querySession = await store.QuerySessionAsync();
        var results = await querySession.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = $name",
            new Dictionary<string, object?> { ["name"] = "Nobody" });

        results.ShouldNotBeNull();
        results.Count.ShouldBe(0);
    }

    [Test]
    public async Task ExecuteSqlAsync_CreatesEntity()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.ExecuteSqlAsync(
            "CREATE person CONTENT { name: 'FromSql', age: 42 }");

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = 'FromSql'");

        results.Count.ShouldBe(1);
        results[0].Age.ShouldBe(42);
    }

    [Test]
    public async Task RawQueryAsync_HandlesNoParameters()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Test", Age = 99 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>("SELECT * FROM person");

        results.ShouldNotBeNull();
        results.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task RawQueryAsync_ReturnsListOfDynamic()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Dynamic", Age = 50 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<dynamic>(
            "SELECT * FROM person WHERE name = 'Dynamic'");

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteSqlAsync_DefinesAndRemovesTable()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Define a temporary table
        var defineResult = await session.ExecuteSqlAsync(
            "DEFINE TABLE temp_test SCHEMALESS;");
        defineResult.ShouldBeGreaterThan(0);

        // Verify it was created
        var checkResult = await session.RawQueryAsync<dynamic>(
            "SELECT * FROM temp_test;");
        checkResult.ShouldNotBeNull();

        // Clean up
        await session.ExecuteSqlAsync("REMOVE TABLE temp_test;");
    }

    [Test]
    public async Task RawQueryAsync_WorksOnQuerySession()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        writeSession.Store(new Person { Name = "QuerySession", Age = 35 });
        await writeSession.SaveChangesAsync();

        await using var querySession = await store.QuerySessionAsync();
        var results = await querySession.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = 'QuerySession'");

        results.Count.ShouldBe(1);
        results[0].Age.ShouldBe(35);
    }
}
