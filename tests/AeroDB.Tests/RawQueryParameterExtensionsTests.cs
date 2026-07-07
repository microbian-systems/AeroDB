using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class RawQueryParameterExtensionsTests
{
    [Test]
    public async Task RawQueryAsync_WithOneParam_FiltersCorrectly()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE age > $p1", 25);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task RawQueryAsync_WithTwoParams_FiltersCorrectly()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE age > $p1 AND name = $p2", 25, "Alice");

        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
        results[0].Age.ShouldBe(30);
    }

    [Test]
    public async Task RawQueryAsync_WithNoParams_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var results = await session.RawQueryAsync<Person>("SELECT * FROM person");

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
    }

    [Test]
    public async Task ExecuteSqlAsync_WithParams_CreatesAndQueriesCorrectly()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var createResult = await session.ExecuteSqlAsync(
            "CREATE person CONTENT { name: $p1, age: $p2 }", "FromParams", 42);

        createResult.ShouldBeGreaterThan(0);

        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = $p1", "FromParams");

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("FromParams");
        results[0].Age.ShouldBe(42);
    }

    [Test]
    public async Task ExecuteSqlAsync_UpdatesDocument()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "UpdateTest", Age = 20 });
        await session.SaveChangesAsync();

        var updateResult = await session.ExecuteSqlAsync(
            "UPDATE person SET age = $p1 WHERE name = $p2", 35, "UpdateTest");

        updateResult.ShouldBeGreaterThan(0);

        var updated = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = $p1", "UpdateTest");

        updated.Count.ShouldBe(1);
        updated[0].Age.ShouldBe(35);
        updated[0].Name.ShouldBe("UpdateTest");
    }

    [Test]
    public async Task RawQueryAsync_WithNullParam()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Test", Age = 30 });
        await session.SaveChangesAsync();

        // Cast to object to force the params overload (otherwise C# prefers
        // the dictionary-based non-params overload when given a null literal)
        var results = await session.RawQueryAsync<Person>(
            "SELECT * FROM person WHERE name = $p1", (object)null!);

        results.ShouldNotBeNull();
        results.Count.ShouldBe(0);
    }
}
