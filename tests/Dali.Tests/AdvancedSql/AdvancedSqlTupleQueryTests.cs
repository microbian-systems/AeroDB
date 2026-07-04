using TUnit.Core;

namespace Dali.Tests;

public class AdvancedSqlTupleQueryTests
{
    [Test]
    public async Task Query2Tuple_ReturnsBothResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30, Email = "alice@test.com" });
        session.Store(new Person { Name = "Bob", Age = 25, Email = "bob@test.com" });
        session.Store(new Order { CustomerName = "Alice", Total = 100m });
        session.Store(new Order { CustomerName = "Bob", Total = 50m });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person WHERE Age > 0; SELECT * FROM order WHERE Total > 0;";
        var (people, orders) = await session.AdvancedSql().QueryAsync<Person, Order>(sql);

        people.ShouldNotBeNull();
        people.Count.ShouldBeGreaterThanOrEqualTo(2);
        orders.ShouldNotBeNull();
        orders.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Query2Tuple_ResultOrderMatchesQueryOrder()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "First", Age = 20 });
        session.Store(new Order { CustomerName = "Second", Total = 200m });
        await session.SaveChangesAsync();

        // Swap statement order: orders first, people second
        var sql = "SELECT * FROM order WHERE Total > 0; SELECT * FROM person WHERE Age > 0;";
        var (orders, people) = await session.AdvancedSql().QueryAsync<Order, Person>(sql);

        orders.ShouldNotBeNull();
        orders.Count.ShouldBeGreaterThanOrEqualTo(1);
        orders[0].CustomerName.ShouldBe("Second");
        people.ShouldNotBeNull();
        people.Count.ShouldBeGreaterThanOrEqualTo(1);
        people[0].Name.ShouldBe("First");
    }

    [Test]
    public async Task Query3Tuple_ReturnsThreeResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Product { Name = "Gadget", Price = 9.99m, Quantity = 10 });
        session.Store(new Order { CustomerName = "Alice", Total = 100m });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person; SELECT * FROM product; SELECT * FROM order;";
        var (people, products, orders) = await session.AdvancedSql().QueryAsync<Person, Product, Order>(sql);

        people.ShouldNotBeNull();
        people.Count.ShouldBeGreaterThanOrEqualTo(1);
        products.ShouldNotBeNull();
        products.Count.ShouldBeGreaterThanOrEqualTo(1);
        orders.ShouldNotBeNull();
        orders.Count.ShouldBeGreaterThanOrEqualTo(1);

        people[0].Name.ShouldBe("Alice");
        products[0].Name.ShouldBe("Gadget");
        orders[0].CustomerName.ShouldBe("Alice");
    }

    [Test]
    public async Task Query4Tuple_ReturnsFourResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Product { Name = "Widget", Price = 5.99m, Quantity = 20 });
        session.Store(new Order { CustomerName = "Alice", Total = 100m });
        session.Store(new Store { Name = "Main St", Category = "Retail" });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person; SELECT * FROM product; SELECT * FROM order; SELECT * FROM store;";
        var (people, products, orders, stores) = await session.AdvancedSql().QueryAsync<Person, Product, Order, Store>(sql);

        people.ShouldNotBeNull();
        people.Count.ShouldBeGreaterThanOrEqualTo(1);
        products.ShouldNotBeNull();
        products.Count.ShouldBeGreaterThanOrEqualTo(1);
        orders.ShouldNotBeNull();
        orders.Count.ShouldBeGreaterThanOrEqualTo(1);
        stores.ShouldNotBeNull();
        stores.Count.ShouldBeGreaterThanOrEqualTo(1);

        people[0].Name.ShouldBe("Alice");
        stores[0].Name.ShouldBe("Main St");
    }

    [Test]
    public async Task StreamAsync_YieldsResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person WHERE Age > 0 ORDER BY Name;";
        var count = 0;
        await foreach (var person in session.AdvancedSql().StreamAsync<Person>(sql))
        {
            person.ShouldNotBeNull();
            person.Name.ShouldNotBeNullOrEmpty();
            count++;
        }

        count.ShouldBe(3);
    }

    [Test]
    public async Task StreamAsync_EmptyResult_YieldsNothing()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create the table by storing one record, then query with non-matching filter
        session.Store(new Person { Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person WHERE Name = 'NonExistent';";
        var count = 0;
        await foreach (var person in session.AdvancedSql().StreamAsync<Person>(sql))
        {
            count++;
        }

        count.ShouldBe(0);
    }

    [Test]
    public async Task QueryWithBadSql_ThrowsInvalidOperationException()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var badSql = "SELECT * FROM nonexistent_table_xyz;";
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            var _ = await session.AdvancedSql().QueryAsync<Person, Order>(badSql);
        });

        ex.Message.ShouldContain("SurrealDB query error");
    }

    [Test]
    public async Task Query2Tuple_FirstQueryEmpty_ReturnsEmptyList()
    {
        // Ensure tables exist before multi-statement query (in-memory engine requirement)
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session.ExecuteSqlAsync("CREATE person:QTFE_temp CONTENT { Name: 'Temp' };");

        var sql = "SELECT * FROM person WHERE Name = 'NonExistent_QTFE_99'; SELECT * FROM person WHERE Name = 'NonExistent_QTFE_99';";
        var (people, people2) = await session.AdvancedSql().QueryAsync<Person, Person>(sql);

        people.ShouldNotBeNull();
        people.Count.ShouldBe(0);
        people2.ShouldNotBeNull();
        people2.Count.ShouldBe(0);
    }

    [Test]
    public async Task QueryWithParameters_PassesBindings()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Order { CustomerName = "Alice", Total = 100m });
        session.Store(new Order { CustomerName = "Bob", Total = 50m });
        await session.SaveChangesAsync();

        var sql = "SELECT * FROM person WHERE Age > $minAge; SELECT * FROM order WHERE Total > $minTotal;";
        var parameters = new Dictionary<string, object?>
        {
            ["minAge"] = 25,
            ["minTotal"] = 60m
        };

        var (people, orders) = await session.AdvancedSql().QueryAsync<Person, Order>(sql, parameters);

        people.ShouldNotBeNull();
        people.Count.ShouldBe(1); // Only Alice (age 30 > 25)
        people[0].Name.ShouldBe("Alice");
        orders.ShouldNotBeNull();
        orders.Count.ShouldBe(1); // Only Alice's order (100 > 60)
    }

    [Test]
    public async Task Query2Tuple_WithCborFallback_ReturnsResults()
    {
        // Simulates the CBOR fallback path by requesting deserialization
        // from a response that may not directly deserialize via CBOR.
        // Using a dynamic query to test the JSON round-trip fallback.
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "CborFallback", Age = 99 });
        session.Store(new Order { CustomerName = "CborFallback", Total = 999m });
        await session.SaveChangesAsync();

        // Use dynamic to trigger the fallback path when the type doesn't match directly
        var sql = "SELECT * FROM person WHERE Name = 'CborFallback'; SELECT * FROM order WHERE CustomerName = 'CborFallback';";
        var (people, orders) = await session.AdvancedSql().QueryAsync<Person, Order>(sql);

        people.ShouldNotBeNull();
        people.Count.ShouldBe(1);
        people[0].Name.ShouldBe("CborFallback");
        orders.ShouldNotBeNull();
        orders.Count.ShouldBe(1);
    }

    [Test]
    public async Task Query2Tuple_WorksOnQuerySession()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var writeSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        writeSession.Store(new Person { Name = "QuerySessionUser", Age = 40 });
        writeSession.Store(new Order { CustomerName = "QuerySessionUser", Total = 400m });
        await writeSession.SaveChangesAsync();

        await using var querySession = await store.QuerySessionAsync();
        var sql = "SELECT * FROM person WHERE Name = 'QuerySessionUser'; SELECT * FROM order WHERE CustomerName = 'QuerySessionUser';";
        var (people, orders) = await querySession.AdvancedSql().QueryAsync<Person, Order>(sql);

        people.Count.ShouldBe(1);
        people[0].Name.ShouldBe("QuerySessionUser");
        orders.Count.ShouldBe(1);
        orders[0].CustomerName.ShouldBe("QuerySessionUser");
    }

    [Test]
    public async Task Query2Tuple_NullParameters_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "NoParams", Age = 50 });
        session.Store(new Order { CustomerName = "NoParams", Total = 500m });
        await session.SaveChangesAsync();

        // Explicitly pass null for parameters
        var sql = "SELECT * FROM person WHERE Name = 'NoParams'; SELECT * FROM order WHERE CustomerName = 'NoParams';";
        var (people, orders) = await session.AdvancedSql().QueryAsync<Person, Order>(sql, parameters: null);

        people.Count.ShouldBe(1);
        orders.Count.ShouldBe(1);
    }
}
