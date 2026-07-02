namespace Dali.Tests;

internal sealed class CounterAggregate
{
    public int Count { get; set; }
    public void Apply(OrderCreated e) => Count++;
    public void Apply(ItemAdded e) => Count++;
}

public class LiveStreamAggregationTests
{
    [Test]
    public async Task LiveStreamAggregation_counts_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = "counter-1";
        await session.Events.StartStream(sid, [new OrderCreated { OrderId = "1" }, new ItemAdded { Sku = "A", Quantity = 1 }]);

        var result = await session.Events.AggregateAsync<CounterAggregate>(sid);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task LiveStreamAggregation_empty_stream_returns_new()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var result = await session.Events.AggregateAsync<CounterAggregate>("nonexistent");
        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
    }
}
