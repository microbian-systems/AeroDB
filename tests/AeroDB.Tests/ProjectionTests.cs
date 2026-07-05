using AeroDB;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

// ─── Test event types ──────────────────────────────────────────────

public class OrderEvent
{
    public string StreamId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
}

public class ShipmentEvent
{
    public string StreamId { get; set; } = "";
    public string TrackingCode { get; set; } = "";
}

// ─── Test projected document ───────────────────────────────────────

public class OrderSummary : Record
{
    public string OrderId { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public int EventCount { get; set; }
}

// ─── Test inline projection ────────────────────────────────────────

public class OrderSummaryProjection : SingleStreamProjection<OrderSummary>
{
    public override Type[] EventTypes => [typeof(OrderEvent)];

    protected override OrderSummary? ApplyEvents(OrderSummary? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new OrderSummary();

        foreach (var evt in events)
        {
            if (evt is OrderEvent oe)
            {
                aggregate.OrderId = oe.OrderId;
                aggregate.TotalAmount += oe.Amount;
                aggregate.EventCount++;
            }
        }

        return aggregate;
    }
}

// ─── Test async projection ─────────────────────────────────────────

public class AsyncOrderSummaryProjection : SingleStreamProjection<OrderSummary>
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
                aggregate.OrderId = oe.OrderId;
                aggregate.TotalAmount += oe.Amount;
                aggregate.EventCount++;
            }
        }

        return aggregate;
    }
}

// ─── Tests ─────────────────────────────────────────────────────────

public class ProjectionTests
{
    [Test]
    public async Task Inline_projection_runs_with_saveChanges()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Register the inline projection
        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        // Append events (triggers tracking via the session's Events property)
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-001", Amount = 100.50m }
        ]);

        // Save changes — inline projection should run and create the OrderSummary
        await session.SaveChangesAsync();

        // Verify the projected document was created
        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-001");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(100.50m);
        found.EventCount.ShouldBe(1);
    }

    [Test]
    public async Task Inline_projection_accumulates_multiple_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-002", Amount = 50.00m }
        ]);
        await session.SaveChangesAsync();

        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-002", Amount = 25.00m }
        ]);
        await session.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-002");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(75.00m);
        found.EventCount.ShouldBe(2);
    }

    [Test]
    public async Task Inline_projection_skips_nonmatching_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Register projection that only handles OrderEvent
        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"ship-{Guid.NewGuid():N}";

        // Append a non-matching event type
        await session.Events.Append(streamId, [
            new ShipmentEvent { StreamId = streamId, TrackingCode = "TRACK-001" }
        ]);

        // Also append a matching event to a different stream
        var orderStreamId = $"order-{Guid.NewGuid():N}";
        await session.Events.Append(orderStreamId, [
            new OrderEvent { StreamId = orderStreamId, OrderId = "ORD-003", Amount = 10.00m }
        ]);

        await session.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();

        // Only the OrderStream events should produce a projection
        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-003");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(10.00m);

        // No summary should exist for the shipment stream
        summaries.Count.ShouldBe(1);
    }

    [Test]
    public async Task Projection_event_types_filtering()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"test-{Guid.NewGuid():N}";

        // Append multiple event types to the same stream
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-004", Amount = 200.00m },
            new ShipmentEvent { StreamId = streamId, TrackingCode = "TRACK-002" }
        ]);

        await session.SaveChangesAsync();

        // The projection should only process OrderEvent, not ShipmentEvent
        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();

        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-004");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(200.00m);
        found.EventCount.ShouldBe(1); // Only OrderEvent counted
    }

    [Test]
    public async Task AsyncDaemon_starts_and_stops()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new AsyncOrderSummaryProjection());

        // Append some events first
        var streamId = $"async-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ASYNC-001", Amount = 300.00m }
        ]);
        await session.SaveChangesAsync();

        // Create and start the daemon
        var daemon = new AsyncDaemon(store, store.Options.Projections);
        daemon.Start(TimeSpan.FromMilliseconds(50));

        // Let it run briefly
        await Task.Delay(200);

        // Stop cleanly
        await daemon.StopAsync();

        // No exception means success
    }

    [Test]
    public async Task Inline_projection_with_no_events_does_nothing()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrderSummaryProjection());

        // Save with no events — projection should not run
        var count = await session.SaveChangesAsync();
        count.ShouldBe(0);
    }

    [Test]
    public async Task Inline_projection_preserves_aggregate_across_saves()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"preserve-{Guid.NewGuid():N}";

        // First append: creates aggregate
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-PRESERVE", Amount = 50.00m }
        ]);
        await session.SaveChangesAsync();

        // Second append in a new session: the projection loads existing aggregate and adds to it
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-PRESERVE", Amount = 75.00m }
        ]);
        await session2.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-PRESERVE");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(125.00m);
        found.EventCount.ShouldBe(2);
    }
}
