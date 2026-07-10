using AeroDB.Sable;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

// ─── Test event types (unique names to avoid collisions) ───────────

internal sealed class EvtOrderPlaced
{
    public string StreamId { get; set; } = "";
    public decimal Amount { get; set; }
}

internal sealed class EvtOrderShipped
{
    public string StreamId { get; set; } = "";
}

internal sealed class EvtOrderCancelled
{
    public string StreamId { get; set; } = "";
}

// ─── Test projected document ───────────────────────────────────────

internal sealed class OrdAggregate : Record
{
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
}

// ─── Test projection (partial for future source-gen support) ───────

internal partial class OrdProjection : EventProjection<OrdAggregate>
{
    public override Type[] EventTypes => [typeof(EvtOrderPlaced), typeof(EvtOrderShipped), typeof(EvtOrderCancelled)];

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events[0] is IEvent ievt) return ievt.StreamId;
        // Fallback: reflection on StreamId property (for raw event data)
        var evt = events[0];
        var prop = evt.GetType().GetProperty("StreamId");
        return prop?.GetValue(evt)?.ToString() ?? "order-1";
    }

    // Create — invoked for the first event when aggregate is null
    private OrdAggregate Create(EvtOrderPlaced e, CancellationToken ct) =>
        new() { Total = e.Amount, Status = "Placed" };

    // Apply — updates an existing aggregate
    private OrdAggregate Apply(EvtOrderShipped e, OrdAggregate a, CancellationToken ct)
    {
        a.Status = "Shipped";
        return a;
    }

    // ShouldDelete — returning null means delete the aggregate
    private OrdAggregate? ShouldDelete(EvtOrderCancelled e, OrdAggregate a, CancellationToken ct) => null;

    // Public wrappers for test access to protected ApplyEvents
    public OrdAggregate? TestApplyEvents(OrdAggregate? aggregate, IReadOnlyList<object> events, CancellationToken ct)
        => ApplyEvents(aggregate, events, ct);
}

// ─── Self-aggregating type for snapshot projection test ────────────

internal sealed class SelfAggregatingOrder : Record
{
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public int Version { get; set; }

    public void Apply(EvtOrderPlaced e)
    {
        Total = e.Amount;
        Status = "Placed";
        Version++;
    }

    public void Apply(EvtOrderShipped e)
    {
        Status = "Shipped";
        Version++;
    }
}

// ─── Test wrapper for SnapshotProjection access ────────────────────

internal sealed class TestSnapshotProjection<T> : SnapshotProjection<T> where T : Record, new()
{
    public TestSnapshotProjection(SnapshotOptions? options = null) : base(options) { }
    public T? TestApplyEvents(T? aggregate, IReadOnlyList<object> events, CancellationToken ct)
        => ApplyEvents(aggregate, events, ct);
}

// ─── Tests ─────────────────────────────────────────────────────────

public class EventProjectionTests
{
    [Test]
    public async Task Create_event_produces_aggregate_with_correct_state()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrdProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        await session.Events.Append(streamId, [
            new EvtOrderPlaced { StreamId = streamId, Amount = 250.00m }
        ]);
        await session.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var aggregates = await qs.Query<OrdAggregate>().ToListAsync();
        var found = aggregates.FirstOrDefault();
        found.ShouldNotBeNull();
        found.Total.ShouldBe(250.00m);
        found.Status.ShouldBe("Placed");
    }

    [Test]
    public async Task Apply_event_updates_existing_aggregate()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrdProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        // Append EvtOrderPlaced (creates aggregate)
        await session.Events.Append(streamId, [
            new EvtOrderPlaced { StreamId = streamId, Amount = 100.00m }
        ]);
        await session.SaveChangesAsync();

        // Append EvtOrderShipped (updates aggregate)
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.Append(streamId, [
            new EvtOrderShipped { StreamId = streamId }
        ]);
        await session2.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var aggregates = await qs.Query<OrdAggregate>().ToListAsync();
        var found = aggregates.FirstOrDefault();
        found.ShouldNotBeNull();
        found.Status.ShouldBe("Shipped");
        found.Total.ShouldBe(100.00m);
    }

    [Test]
    public async Task ShouldDelete_event_removes_aggregate()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrdProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        // Append EvtOrderPlaced (creates aggregate)
        await session.Events.Append(streamId, [
            new EvtOrderPlaced { StreamId = streamId, Amount = 75.00m }
        ]);
        await session.SaveChangesAsync();

        // Verify aggregate exists
        await using var qs1 = await store.QuerySessionAsync();
        var aggregates1 = await qs1.Query<OrdAggregate>().ToListAsync();
        aggregates1.Count.ShouldBe(1);

        // Append EvtOrderCancelled (should delete aggregate)
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.Append(streamId, [
            new EvtOrderCancelled { StreamId = streamId }
        ]);
        await session2.SaveChangesAsync();

        await using var qs2 = await store.QuerySessionAsync();
        var aggregates2 = await qs2.Query<OrdAggregate>().ToListAsync();
        aggregates2.Count.ShouldBe(0);
    }

    [Test]
    public async Task Multiple_events_in_sequence_produce_correct_final_state()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new OrdProjection());

        var streamId = $"order-{Guid.NewGuid():N}";

        // Append multiple events in batch
        await session.Events.Append(streamId, [
            new EvtOrderPlaced { StreamId = streamId, Amount = 500.00m },
            new EvtOrderShipped { StreamId = streamId }
        ]);
        await session.SaveChangesAsync();

        await using var qs = await store.QuerySessionAsync();
        var aggregates = await qs.Query<OrdAggregate>().ToListAsync();
        var found = aggregates.FirstOrDefault();
        found.ShouldNotBeNull();
        found.Status.ShouldBe("Shipped");
        found.Total.ShouldBe(500.00m);
    }

    [Test]
    public async Task Empty_event_list_returns_aggregate_unchanged()
    {
        var projection = new OrdProjection();
        var aggregate = new OrdAggregate { Total = 100m, Status = "Test" };
        var events = Array.Empty<object>();

        var result = projection.TestApplyEvents(aggregate, events, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldBeSameAs(aggregate);
        result.Total.ShouldBe(100m);
        result.Status.ShouldBe("Test");
    }

    [Test]
    public void Snapshot_projection_auto_discovers_event_types()
    {
        var snapshot = new SnapshotProjection<SelfAggregatingOrder>();

        snapshot.EventTypes.ShouldNotBeNull();
        snapshot.EventTypes.Length.ShouldBe(2);
        snapshot.EventTypes.ShouldContain(typeof(EvtOrderPlaced));
        snapshot.EventTypes.ShouldContain(typeof(EvtOrderShipped));
    }

    [Test]
    public void Snapshot_projection_applies_events_to_aggregate()
    {
        var snapshot = new TestSnapshotProjection<SelfAggregatingOrder>();

        var events = new object[]
        {
            new EvtOrderPlaced { Amount = 300.00m },
            new EvtOrderShipped()
        };

        var result = snapshot.TestApplyEvents(null, events, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Status.ShouldBe("Shipped");
        result.Total.ShouldBe(300.00m);
        result.Version.ShouldBe(2);
    }
}
