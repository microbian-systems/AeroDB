using AeroDB;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Diagnostic tests to understand why RebuildAsync isn't persisting documents.
/// </summary>
public class ProjectionRebuildDiagnosticTests
{
    [Test]
    public async Task Debug_RebuildAsync_StepByStep()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        store.Options.Projections.Add(new RebuildableOrderSummaryProjection());

        var streamId = $"debug-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "DBG-001", Amount = 100.00m }
        ]);
        await session.SaveChangesAsync();

        // 1. Verify events are stored
        var fetchedEvents = await session.Events.FetchStream(streamId);
        fetchedEvents.Count.ShouldBe(1);

        // 2. Direct query to mt_events
        var rawResult = await session.RawQueryAsync<object>("SELECT * FROM mt_events WHERE event_type IN ['OrderEvent'] ORDER BY stream_id ASC, version ASC;", null, CancellationToken.None);
        rawResult.Count.ShouldBeGreaterThan(0);

        // 3. Now test the actual session.Store + SaveChangesAsync
        var tableName = "order_summary";
        var rebuildSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var doc = new OrderSummary
        {
            Id = new RecordIdOf<string>(tableName, streamId),
            OrderId = "DBG-001",
            TotalAmount = 100.00m,
            EventCount = 1
        };
        rebuildSession.Store(doc);
        var savedCount = await rebuildSession.SaveChangesAsync();
        savedCount.ShouldBeGreaterThan(0);

        // 4. Query back
        await using var qs = await store.QuerySessionAsync();
        var loaded = await qs.LoadAsync<OrderSummary>(streamId);
        loaded.ShouldNotBeNull();
        loaded.TotalAmount.ShouldBe(100.00m);
        
        // 5. Now try with the projection's rebuild
        var projection = new RebuildableOrderSummaryProjection();
        await using var rs2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await projection.RebuildAsync(rs2, CancellationToken.None);
        var savedAfterRebuild = await rs2.SaveChangesAsync();
        savedAfterRebuild.ShouldBeGreaterThan(0);

        // 6. Query after rebuild
        await using var qs2 = await store.QuerySessionAsync();
        var summaries = await qs2.Query<OrderSummary>().ToListAsync();
        summaries.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
}
