using NSubstitute;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ─── Test event types (unique names to avoid conflicts) ───────────

public class SideEffectTriggered
{
    public string TriggerId { get; set; } = "";
}

public class SideEffectGenerated
{
    public string SourceId { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
}

// ─── Test projected document ───────────────────────────────────────

public class SideEffectDoc : Record
{
    public string Status { get; set; } = "";
}

// ─── Test projection that raises side effects ──────────────────────

internal class SideEffectTestProjection : InlineProjection<SideEffectDoc>
{
    public override Type[] EventTypes => [typeof(SideEffectTriggered)];
    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;

    protected override object GetDocumentId(IReadOnlyList<object> events) => "saga-se-1";

    protected override SideEffectDoc? ApplyEvents(SideEffectDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new SideEffectDoc();
        aggregate.Status = "Triggered";
        return aggregate;
    }

    public override async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        await base.ApplyAsync(context, ct);
        // Raise side effect after base processing
        context.RaiseSideEffect(new AppendEventSideEffect("gen-stream", new SideEffectGenerated
        {
            SourceId = "saga-se-1",
            GeneratedAt = DateTimeOffset.UtcNow
        }));
    }
}

// ─── Test projection that raises multiple side effects ─────────────

internal class MultiSideEffectTestProjection : InlineProjection<SideEffectDoc>
{
    public override Type[] EventTypes => [typeof(SideEffectTriggered)];
    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;

    protected override object GetDocumentId(IReadOnlyList<object> events) => "saga-multi";

    protected override SideEffectDoc? ApplyEvents(SideEffectDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new SideEffectDoc();
        aggregate.Status = "Multi";
        return aggregate;
    }

    public override async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        await base.ApplyAsync(context, ct);
        // Raise two side effects
        context.RaiseSideEffect(new AppendEventSideEffect("stream-a", new SideEffectGenerated
        {
            SourceId = "multi-a",
            GeneratedAt = DateTimeOffset.UtcNow
        }));
        context.RaiseSideEffect(new AppendEventSideEffect("stream-b", new SideEffectGenerated
        {
            SourceId = "multi-b",
            GeneratedAt = DateTimeOffset.UtcNow
        }));
    }
}

// ─── Test projection that chains side effects (reentrant) ─────────

internal class ChainedSideEffectProjection : InlineProjection<SideEffectDoc>
{
    private static int _callCount;

    public override Type[] EventTypes => [typeof(SideEffectTriggered)];
    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;

    protected override object GetDocumentId(IReadOnlyList<object> events) => "saga-chain";

    protected override SideEffectDoc? ApplyEvents(SideEffectDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new SideEffectDoc();
        aggregate.Status = "Chained";
        return aggregate;
    }

    public override async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        await base.ApplyAsync(context, ct);

        Interlocked.Increment(ref _callCount);

        // Only raise side effect on the first two invocations to keep the chain bounded
        if (_callCount < 3)
        {
            context.RaiseSideEffect(new AppendEventSideEffect("chain-stream", new SideEffectTriggered
            {
                TriggerId = $"chain-call-{_callCount}"
            }));
        }
    }

    /// <summary>Reset call counter for test isolation.</summary>
    public static void Reset() => _callCount = 0;
}

// ─── Tests ─────────────────────────────────────────────────────────

public class SideEffectTests
{
    [Test]
    public async Task Projection_raises_side_effect_appends_event()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new SideEffectTestProjection());

        // Append event that triggers the projection
        await session.Events.Append("trigger-1", [
            new SideEffectTriggered { TriggerId = "trig-001" }
        ]);

        await session.SaveChangesAsync();

        // Verify the side-effect event was appended to "gen-stream"
        await using var readSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var genEvents = await readSession.Events.FetchStream("gen-stream");
        genEvents.Count.ShouldBe(1);
        var gen = genEvents[0].Data.ShouldBeOfType<SideEffectGenerated>();
        gen.SourceId.ShouldBe("saga-se-1");
    }

    [Test]
    public async Task Multiple_side_effects_all_processed()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new MultiSideEffectTestProjection());

        await session.Events.Append("trigger-multi", [
            new SideEffectTriggered { TriggerId = "trig-multi" }
        ]);

        await session.SaveChangesAsync();

        // Verify both side-effect events were appended
        await using var readSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var streamA = await readSession.Events.FetchStream("stream-a");
        streamA.Count.ShouldBe(1);
        streamA[0].Data.ShouldBeOfType<SideEffectGenerated>().SourceId.ShouldBe("multi-a");

        var streamB = await readSession.Events.FetchStream("stream-b");
        streamB.Count.ShouldBe(1);
        streamB[0].Data.ShouldBeOfType<SideEffectGenerated>().SourceId.ShouldBe("multi-b");
    }

    [Test]
    public async Task Chained_side_effects_processed_within_depth_limit()
    {
        ChainedSideEffectProjection.Reset();

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new ChainedSideEffectProjection());

        await session.Events.Append("trigger-chain", [
            new SideEffectTriggered { TriggerId = "trig-chain" }
        ]);

        // Should process without throwing (chain is bounded to 2 rounds)
        await session.SaveChangesAsync();

        // Verify the chained events
        await using var readSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var chainEvents = await readSession.Events.FetchStream("chain-stream");
        chainEvents.Count.ShouldBe(2);
    }

    [Test]
    public async Task No_side_effects_normal_processing()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Use a standard projection that doesn't raise side effects
        store.Options.Projections.Add(new OrderSummaryProjection());

        var streamId = $"order-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "ORD-NO-SE", Amount = 75m }
        ]);

        // Should complete without any side-effect processing
        await session.SaveChangesAsync();

        // Verify the projected document was created
        await using var qs = await store.QuerySessionAsync();
        var summaries = await qs.Query<OrderSummary>().ToListAsync();
        var found = summaries.FirstOrDefault(s => s.OrderId == "ORD-NO-SE");
        found.ShouldNotBeNull();
        found.TotalAmount.ShouldBe(75m);
    }

    [Test]
    public async Task RaiseSideEffect_throws_on_null()
    {
        var ctx = new ProjectionContext(
            Substitute.For<IDocumentSession>(),
            Array.Empty<IEvent>().AsReadOnly());

        Should.Throw<ArgumentNullException>(() => ctx.RaiseSideEffect(default(IProjectionSideEffect)!));
    }
}
