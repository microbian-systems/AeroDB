using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

// ─── Test projection that implements IEnrichProjection ──────────────

internal sealed class EnrichedProjection : InlineProjection<TestDoc>, IEnrichProjection
{
    public static string? LastEnrichedData;

    public override Type[] EventTypes => [typeof(OrderCreated)];

    public Task EnrichAsync(IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken ct)
    {
        LastEnrichedData = $"enriched-{events.Count}";
        return Task.CompletedTask;
    }

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events[0] is OrderCreated oc)
            return oc.OrderId;
        return "default-id";
    }

    protected override TestDoc? ApplyEvents(TestDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new TestDoc();
        aggregate.Name = LastEnrichedData ?? "no-enrich";
        aggregate.Count++;
        return aggregate;
    }
}

// ─── Tests ──────────────────────────────────────────────────────────

public class EnrichProjectionTests
{
    [Test]
    public async Task EnrichProjection_called_before_ApplyAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(new EnrichedProjection());
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.Events.StartStream("enrich-stream", [new OrderCreated { StreamId = "enrich-stream", OrderId = "e1" }]);
        await session.SaveChangesAsync();

        EnrichedProjection.LastEnrichedData.ShouldBe("enriched-1");
    }
}
