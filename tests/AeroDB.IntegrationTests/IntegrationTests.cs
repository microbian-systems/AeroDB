using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.IntegrationTests;

/// <summary>
/// Integration tests against a real (remote) SurrealDB instance.
/// These tests validate features that the embedded in-memory engine handles poorly:
///   - FETCH with projections
///   - WHERE id IN [...] (LoadMany with ExecuteSqlAsync-created records)
///   - Event sourcing with DateTimeOffset serialization under SCHEMAFULL
///   - CBOR binary serialization of DateTimeOffset
///
/// These tests are tagged with <see cref="CategoryAttribute"/> so they can be filtered out:
/// <c>dotnet test --filter "Category!=Integration"</c>
///
/// Prerequisite: a SurrealDB instance running at the endpoint configured
/// via <see cref="TestHarness"/> (default ws://localhost:8000).
/// Use <c>docker-compose -f docker-compose.surrealdb.yml up</c> to start one.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class IntegrationTests
{
    /// <summary>
    /// Verifies that FETCH with explicit projections expands related records
    /// on a real SurrealDB instance. The in-memory engine does not resolve
    /// FETCH for typed Record properties in query results.
    /// </summary>
    [Test]
    public async Task Fetch_with_include_expands_related()
    {
        await TestHarness.EnsureAvailableAsync();

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create a customer record
        var customer = new Customer { Name = "Alice", Email = "alice@example.com" };
        session.Store(customer);
        await session.SaveChangesAsync();

        // Create an order referencing the customer via a typed Record property
        var order = new OrderWithCustomer
        {
            Product = "Widget",
            Customer = customer
        };
        session.Store(order);
        await session.SaveChangesAsync();

        // Query with FETCH — the real SurrealDB expands the Customer property
        // Table name is snake_case of the class name: OrderWithCustomer → order_with_customer
        var results = await session.RawQueryAsync<OrderWithCustomer>(
            "SELECT *, ->customer->customer.* FROM order_with_customer FETCH customer;");

        results.ShouldNotBeNull();
        results.Count.ShouldBeGreaterThanOrEqualTo(1);

        var loaded = results.FirstOrDefault(r => r.Product == "Widget");
        loaded.ShouldNotBeNull();

        // On a real SurrealDB, FETCH resolves the Record property inline.
        // The in-memory engine often returns null for the expanded record.
        loaded.Customer.ShouldNotBeNull();
        loaded.Customer.Name.ShouldBe("Alice");
        loaded.Customer.Email.ShouldBe("alice@example.com");
    }

    /// <summary>
    /// Verifies that LoadManyAsync resolves records created via ExecuteSqlAsync.
    /// The embedded in-memory engine has a known limitation where RawQueryAsync
    /// with WHERE id IN [...] doesn't find records created via raw CREATE statements.
    /// A real SurrealDB handles this correctly.
    /// </summary>
    [Test]
    public async Task LoadMany_resolves_execute_sql_created_records()
    {
        await TestHarness.EnsureAvailableAsync();

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create records using raw SurrealQL
        await session.ExecuteSqlAsync("CREATE test_person:alice CONTENT { name: 'Alice', age: 30, email: 'alice@example.com' };");
        await session.ExecuteSqlAsync("CREATE test_person:bob CONTENT { name: 'Bob', age: 25, email: 'bob@example.com' };");

        // LoadManyAsync uses WHERE id IN [...] internally
        var results = await session.LoadManyAsync<TestPerson>(new[] { "alice", "bob" });

        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.Any(p => p.Name == "Alice").ShouldBeTrue();
        results.Any(p => p.Name == "Bob").ShouldBeTrue();
    }

    /// <summary>
    /// Verifies that event sourcing with Events.Enabled=true works correctly
    /// with DateTimeOffset properties under SCHEMAFULL table definition.
    /// The in-memory engine has serialization issues with DateTimeOffset in
    /// CBOR mode when SCHEMAFULL is enforced.
    /// </summary>
    [Test]
    public async Task Events_enabled_with_datetimeoffset_and_schemafull()
    {
        await TestHarness.EnsureAvailableAsync();

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Events.Enabled = true;
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Append an event that contains DateTimeOffset data
        var streamId = Guid.NewGuid().ToString();
        var timestamp = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromHours(2));
        var events = new[]
        {
            new OrderSubmitted
            {
                OrderId = "ORD-001",
                SubmittedAt = timestamp,
                CustomerName = "Alice"
            }
        };

        // Append to the event stream (immediate — no SaveChangesAsync needed)
        await session.Events.StartStream(streamId, events);

        // Fetch the stream and verify the event round-trips correctly
        var streamState = await session.Events.FetchStream(streamId);
        streamState.ShouldNotBeNull();
        streamState.Count.ShouldBe(1);

        var submitted = streamState[0].Data as OrderSubmitted;
        submitted.ShouldNotBeNull();
        submitted.OrderId.ShouldBe("ORD-001");
        submitted.CustomerName.ShouldBe("Alice");

        // DateTimeOffset should round-trip correctly on a real SurrealDB
        submitted.SubmittedAt.ShouldBe(timestamp);
    }

    /// <summary>
    /// Verifies that CBOR binary serialization round-trips DateTimeOffset correctly
    /// on a real SurrealDB. The in-memory engine has known issues with CBOR
    /// serialization of DateTimeOffset values.
    /// </summary>
    [Test]
    public async Task Binary_serialization_roundtrips_datetimeoffset()
    {
        await TestHarness.EnsureAvailableAsync();

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var original = new Product
        {
            Name = "Test Product",
            Price = 19.99m,
            Quantity = 5,
            Category = "Test",
            CreatedAt = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.FromHours(-5))
        };

        session.Store(original);
        await session.SaveChangesAsync();

        // Use the mapped query API so the predicate follows Sable's field naming.
        var loaded = await session.Query<Product>()
            .Where(product => product.Name == "Test Product")
            .ToListAsync();
        loaded.ShouldNotBeNull();
        loaded.Count.ShouldBeGreaterThanOrEqualTo(1);
        loaded[0].Name.ShouldBe("Test Product");
        loaded[0].CreatedAt.ShouldBe(original.CreatedAt);
        loaded[0].Price.ShouldBe(19.99m);
    }
}
