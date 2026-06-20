using TUnit.Core;

namespace Dali.Tests;

public class OrderCreated
{
    public string OrderId { get; set; } = "";
}

public class ItemAdded
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}

public class EventStoreTests
{
    [Test]
    public async Task StartStream_appends_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var streamId = $"order-{Guid.NewGuid():N}";
        await session.Events.StartStream(streamId, [
            new OrderCreated { OrderId = streamId },
            new ItemAdded { Sku = "ABC", Quantity = 2 }
        ]);
    }

    [Test]
    public async Task Append_to_existing_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var streamId = $"stream-{Guid.NewGuid():N}";
        await session.Events.StartStream(streamId, [new OrderCreated { OrderId = streamId }]);
        await session.Events.Append(streamId, [new ItemAdded { Sku = "XYZ", Quantity = 1 }]);
    }

    [Test]
    public async Task Fetch_nonexistent_stream_returns_empty()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var fetched = await session.Events.FetchStream("nonexistent");
        fetched.Count.ShouldBe(0);
    }
}
