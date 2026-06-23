using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ─── Additional test event types ────────────────────────────────────

public class OrderShipped
{
    public string StreamId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public DateTimeOffset ShippedAt { get; set; }
}

// ─── Test projection that rebuilds from events ──────────────────────

public class RebuildableOrderSummaryProjection : SingleStreamProjection<OrderSummary>
{
    public override Type[] EventTypes => [typeof(OrderEvent), typeof(OrderShipped)];

    protected override OrderSummary? ApplyEvents(OrderSummary? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new OrderSummary();

        foreach (var evt in events)
        {
            if (evt is OrderEvent oe)
            {
                if (string.IsNullOrEmpty(aggregate.OrderId))
                    aggregate.OrderId = oe.OrderId;
                aggregate.TotalAmount += oe.Amount;
                aggregate.EventCount++;
            }
            else if (evt is OrderShipped)
            {
                aggregate.EventCount++;
            }
        }

        return aggregate;
    }
}

public class AsyncRebuildableProjection : SingleStreamProjection<OrderSummary>
{
    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
    public override Type[] EventTypes => [typeof(OrderEvent)];

    protected override OrderSummary? ApplyEvents(OrderSummary? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new OrderSummary();

        foreach (var evt in events)
        {
            if (evt is OrderEvent oe)
            {
                if (string.IsNullOrEmpty(aggregate.OrderId))
                    aggregate.OrderId = oe.OrderId;
                aggregate.TotalAmount += oe.Amount;
                aggregate.EventCount++;
            }
        }

        return aggregate;
    }
}

// ─── Test: ShipmentOnly projection (different doc type) ──────────────

public class ShipmentRecord : Record
{
    public string TrackingCode { get; set; } = "";
    public int EventCount { get; set; }
}

public class ShipmentProjection : SingleStreamProjection<ShipmentRecord>
{
    public override Type[] EventTypes => [typeof(ShipmentEvent)];

    protected override ShipmentRecord? ApplyEvents(ShipmentRecord? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new ShipmentRecord();

        foreach (var evt in events)
        {
            if (evt is ShipmentEvent se)
            {
                aggregate.TrackingCode = se.TrackingCode;
                aggregate.EventCount++;
            }
        }

        return aggregate;
    }
}

// ─── Tests ──────────────────────────────────────────────────────────

public class ProjectionRebuildTests
{
    // ── Configuration tests ──────────────────────────────────────────

    [Test]
    public async Task ProjectionOptions_RebuildOnStartup_Defaults_False()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.RebuildOnStartup.ShouldBeFalse();
    }

    [Test]
    public async Task ProjectionOptions_RebuildProjectionNames_Defaults_Empty()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.RebuildProjectionNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ProjectionOptions_EnsureStateTable_Defaults_True()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.EnsureStateTable.ShouldBeTrue();
    }

    [Test]
    public async Task ProjectionOptions_Can_Set_RebuildOnStartup()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.RebuildOnStartup = true;
        options.ProjectionBuild.RebuildOnStartup.ShouldBeTrue();
    }

    [Test]
    public async Task ProjectionOptions_Can_Set_RebuildProjectionNames()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.RebuildProjectionNames = ["OrderSummaryProjection"];
        options.ProjectionBuild.RebuildProjectionNames.Length.ShouldBe(1);
        options.ProjectionBuild.RebuildProjectionNames[0].ShouldBe("OrderSummaryProjection");
    }

    [Test]
    public async Task ProjectionOptions_Can_Set_EnsureStateTable()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.EnsureStateTable = false;
        options.ProjectionBuild.EnsureStateTable.ShouldBeFalse();
    }

    [Test]
    public async Task StoreOptions_ProjectionBuild_Exists()
    {
        var options = new StoreOptions();
        options.ProjectionBuild.ShouldNotBeNull();
        options.ProjectionBuild.ShouldBeOfType<ProjectionOptions>();
    }

    // ── Projection lifecycle tests ──────────────────────────────────

    [Test]
    public async Task InlineProjection_Lifecycle_Default_Is_Inline()
    {
        var projection = new RebuildableOrderSummaryProjection();
        projection.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    [Test]
    public async Task InlineProjection_Lifecycle_Can_Set_To_Async()
    {
        var projection = new AsyncRebuildableProjection();
        projection.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }

    // ── Rebuild functional tests ────────────────────────────────────

    [Test]
    public async Task RebuildAsync_Replays_Events_And_Creates_Document()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        // Register projection and enable events
        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        // Append events to the event store
        var streamId = $"rebuild-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "REB-001", Amount = 100.00m },
            new OrderEvent { StreamId = streamId, OrderId = "REB-001", Amount = 50.00m }
        ]);
        await session.SaveChangesAsync();

        // Now rebuild from scratch
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        // Verify the projected document was created
        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "REB-001");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(150.00m);
        found.EventCount.ShouldBe(2);
    }

    [Test]
    public async Task RebuildAsync_Idempotent_Calling_Twice_Produces_Same_Result()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        var streamId = $"idempotent-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "IDM-001", Amount = 75.00m }
        ]);
        await session.SaveChangesAsync();
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "IDM-001", Amount = 25.00m }
        ]);
        await session.SaveChangesAsync();

        // First rebuild
        var projection = new RebuildableOrderSummaryProjection();
        await using var rs1 = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rs1, CancellationToken.None);
        await rs1.SaveChangesAsync();

        // Second rebuild
        await using var rs2 = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rs2, CancellationToken.None);
        await rs2.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "IDM-001");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(100.00m);
        found.EventCount.ShouldBe(2);
    }

    [Test]
    public async Task RebuildAsync_Empty_Events_Does_Nothing()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        // No events appended — rebuild should do nothing
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        summaries.Count.ShouldBe(0);
    }

    [Test]
    public async Task RebuildAsync_Preserves_Existing_Documents_Of_Other_Types()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());
        store.Options.Projections.Add(new ShipmentProjection());

        // Create a Person document (not related to any projection)
        var personId = $"person-{Guid.NewGuid():N}";
        session.Store(new Person { Id = new RecordIdOf<string>("person", personId), Name = "Alice", Age = 30 });
        await session.SaveChangesAsync();

        // Append events for the order projection
        var streamId = $"order-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "PRES-001", Amount = 200.00m }
        ]);
        await session.SaveChangesAsync();

        // Rebuild only the order projection
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        // Person should still exist
        await using var qs = await store.QuerySessionAsync();
        var person = await qs.LoadAsync<Person>(personId);
        person.ShouldNotBeNull();
        person.Name.ShouldBe("Alice");

        // Order summary should exist
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        summaries.Count.ShouldBe(1);
        summaries[0].TotalAmount.ShouldBe(200.00m);
    }

    [Test]
    public async Task RebuildAsync_Multiple_Streams()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        // Append events to two separate streams
        var streamA = $"multi-A-{Guid.NewGuid():N}";
        var streamB = $"multi-B-{Guid.NewGuid():N}";

        await session.Events.Append(streamA, [
            new OrderEvent { StreamId = streamA, OrderId = "MULTI-A", Amount = 100.00m }
        ]);
        await session.Events.Append(streamB, [
            new OrderEvent { StreamId = streamB, OrderId = "MULTI-B", Amount = 200.00m },
            new OrderEvent { StreamId = streamB, OrderId = "MULTI-B", Amount = 50.00m }
        ]);
        await session.SaveChangesAsync();

        // Rebuild
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        summaries.Count.ShouldBe(2);

        var a = summaries.First(s => s.OrderId == "MULTI-A");
        a.TotalAmount.ShouldBe(100.00m);
        a.EventCount.ShouldBe(1);

        var b = summaries.First(s => s.OrderId == "MULTI-B");
        b.TotalAmount.ShouldBe(250.00m);
        b.EventCount.ShouldBe(2);
    }

    [Test]
    public async Task RebuildAsync_Multiple_Event_Types()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        var streamId = $"multi-type-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "MULTITYPE", Amount = 100.00m },
            new OrderShipped { StreamId = streamId, OrderId = "MULTITYPE", ShippedAt = DateTimeOffset.UtcNow }
        ]);
        await session.SaveChangesAsync();

        // Rebuild
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "MULTITYPE");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(100.00m);
        found.EventCount.ShouldBe(2); // OrderEvent + OrderShipped
    }

    [Test]
    public async Task RebuildAsync_Replaces_Existing_Projection_Data()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        // First: append events and save (inline projection creates the doc)
        var streamId = $"replace-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "REPL", Amount = 50.00m }
        ]);
        await session.SaveChangesAsync();

        // Second: rebuild should recreate from scratch
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "REPL");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(50.00m);
        found.EventCount.ShouldBe(1);
    }

    [Test]
    public async Task RebuildAsync_With_Async_Lifecycle_Projection()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new AsyncRebuildableProjection());

        var streamId = $"async-rebuild-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ASYNC-REB", Amount = 300.00m }
        ]);
        await session.SaveChangesAsync();

        // Even though this is an Async lifecycle projection, we can still rebuild it
        var projection = new AsyncRebuildableProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "ASYNC-REB");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(300.00m);
        found.EventCount.ShouldBe(1);
    }

    [Test]
    public async Task RebuildAsync_Handles_Different_Projection_Type()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new ShipmentProjection());

        var streamId = $"ship-rebuild-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new ShipmentEvent { StreamId = streamId, TrackingCode = "TRACK-REBUILD" }
        ]);
        await session.SaveChangesAsync();

        var projection = new ShipmentProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var records = await qs.Query<ShipmentRecord>().ToListAsync();
        var found = records.FirstOrDefault(s => s.TrackingCode == "TRACK-REBUILD");
        found.ShouldNotBeNull();
        found.EventCount.ShouldBe(1);
    }

    [Test]
    public async Task EnsureProjectionStateTableAsync_Executes_Without_Error()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var schemaManager = new SchemaManager();
        await schemaManager.EnsureProjectionStateTableAsync(
            ((InternalSessionBase)session).Session, CancellationToken.None);

        // Verify the table was created by querying it
        var result = await session.RawQueryAsync<object>(
            "SELECT * FROM mt_projection_progress;", null, CancellationToken.None);
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task EnsureProjectionStateTableAsync_Is_Idempotent()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)session).Session;

        // Call twice — should not throw
        await schemaManager.EnsureProjectionStateTableAsync(surrealSession, CancellationToken.None);
        await schemaManager.EnsureProjectionStateTableAsync(surrealSession, CancellationToken.None);
    }

    [Test]
    public async Task RebuildAsync_With_No_Projection_Registration_Still_Works()
    {
        // The projection can be rebuilt even if not registered in store.Options.Projections
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var streamId = $"unreg-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "UNREG", Amount = 42.00m }
        ]);
        await session.SaveChangesAsync();

        // Create a separate projection instance (not registered)
        var projection = new RebuildableOrderSummaryProjection();
        await using var rebuildSession = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rebuildSession, CancellationToken.None);
        await rebuildSession.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "UNREG");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(42.00m);
    }

    [Test]
    public async Task RebuildAsync_Clears_Then_Rebuilds()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());
        store.Options.Projections.Add(new ShipmentProjection());

        var streamId = $"clear-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "CLEAR", Amount = 100.00m }
        ]);
        await session.SaveChangesAsync();

        // Rebuild once
        var projection = new RebuildableOrderSummaryProjection();
        await using var rs1 = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rs1, CancellationToken.None);
        await rs1.SaveChangesAsync();

        // Add more events to the same stream
        await using var session2 = await store.LightweightSessionAsync();
        await session2.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "CLEAR", Amount = 50.00m }
        ]);
        await session2.SaveChangesAsync();

        // Rebuild again — should clear old doc and reconstruct from all events
        await using var rs2 = await store.LightweightSessionAsync();
        await projection.RebuildAsync(rs2, CancellationToken.None);
        await rs2.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "CLEAR");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(150.00m); // 100 + 50 = both events replayed
        found.EventCount.ShouldBe(2);
    }
}
