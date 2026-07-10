using AeroDB.Sable;
using SurrealDb.Embedded.InMemory;
using TUnit.Core;

namespace AeroDB.Tests;

public class OrderCreated
{
    public string StreamId { get; set; } = "";
    public string OrderId { get; set; } = "";
}

public class ItemAdded
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>
/// Projected document returned by FetchLatest.
/// The stream ID becomes the document ID for SingleStreamProjection.
/// </summary>
public class TestDoc : SurrealDb.Net.Models.Record
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>Test event used with FetchLatest tests.</summary>
public class FetchTestEvent
{
    public string StreamId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Test projection that creates a TestDoc document from FetchTestEvent events.
/// </summary>
public class FetchTestProjection : SingleStreamProjection<TestDoc>
{
    public override Type[] EventTypes => [typeof(FetchTestEvent)];

    protected override TestDoc? ApplyEvents(TestDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new TestDoc();
        foreach (var evt in events)
        {
            if (evt is FetchTestEvent te)
            {
                aggregate.Name = te.Name;
                aggregate.Count++;
            }
        }
        return aggregate;
    }
}

/// <summary>Test event for typed StartStream tests.</summary>
public class TypedStreamTestEvent
{
    public string Name { get; set; } = "";
}

/// <summary>Another test event for typed StartStream with Guid.</summary>
public class TypedStreamMyEvent
{
    public string Name { get; set; } = "";
}

public class EventStoreTests
{
    [Test]
    public async Task StartStream_appends_events()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var streamId = $"stream-{Guid.NewGuid():N}";
        await session.Events.StartStream(streamId, [new OrderCreated { OrderId = streamId }]);
        await session.Events.Append(streamId, [new ItemAdded { Sku = "XYZ", Quantity = 1 }]);
    }

    [Test]
    public async Task Fetch_nonexistent_stream_returns_empty()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var fetched = await session.Events.FetchStream("nonexistent");
        fetched.Count.ShouldBe(0);
    }

    [Test]
    public async Task FetchLatest_returns_projected_document()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Register a projection
        store.Options.Projections.Add(new FetchTestProjection());

        var streamId = $"test-{Guid.NewGuid():N}";
        await session.Events.StartStream(streamId, [new FetchTestEvent { StreamId = streamId, Name = "fetch-latest-test" }]);
        await session.SaveChangesAsync();

        // The projection should have created a TestDoc with the streamId as document ID
        await using var query = await store.QuerySessionAsync();
        var doc = await query.FetchLatest<TestDoc>(streamId);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("fetch-latest-test");
        doc.Count.ShouldBe(1);
    }

    [Test]
    public async Task FetchLatest_returns_null_for_nonexistent_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var query = await store.QuerySessionAsync();

        var doc = await query.FetchLatest<TestDoc>("nonexistent-stream");
        doc.ShouldBeNull();
    }

    [Test]
    public async Task FetchLatest_with_Guid_works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        store.Options.Projections.Add(new FetchTestProjection());

        var streamId = Guid.NewGuid();
        var streamIdStr = streamId.ToString("D");
        await session.Events.StartStream(streamIdStr, [new FetchTestEvent { StreamId = streamIdStr, Name = "guid-test" }]);
        await session.SaveChangesAsync();

        await using var query = await store.QuerySessionAsync();
        var doc = await query.FetchLatest<TestDoc>(streamId);
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("guid-test");
        doc.Count.ShouldBe(1);
    }

    [Test]
    public async Task StartStream_with_type_parameter_works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        
        var streamId = await session.Events.StartStream<string>("typed-stream", [new TestEvent("test")]);
        streamId.ShouldBe("typed-stream");
    }

    [Test]
    public async Task StartStream_with_type_and_guid_works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var guid = Guid.NewGuid();
        var streamId = await session.Events.StartStream<TypedStreamMyEvent>(guid, [new TypedStreamMyEvent { Name = "test" }]);
        streamId.ShouldBe(guid.ToString("D"));
    }

    [Test]
    public async Task Quick_mode_events_have_zero_sequence()
    {
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.Namespace = "test";
            o.Database = "test";
            o.Events.AppendMode = EventAppendMode.Quick;
        });
        await store.InitializeAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session.Events.StartStream("qs-1", [new TypedStreamTestEvent { Name = "quick" }]);
        var events = await session.Events.FetchStream("qs-1");
        events[0].Sequence.ShouldBe(0);
    }

    [Test]
    public void Rich_mode_is_default()
    {
        var options = new EventSourcingOptions();
        options.AppendMode.ShouldBe(EventAppendMode.Rich);
    }

    [Test]
    public async Task AppendExclusive_succeeds_on_empty_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = Guid.NewGuid().ToString("N");
        await session.Events.AppendExclusive(sid, [new TestEvent("exclusive")]);
    }

    [Test]
    public async Task AppendExclusive_throws_on_existing_stream()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]);
        Should.Throw<ConcurrencyException>(async () =>
            await session.Events.AppendExclusive(sid, [new TestEvent("second")]));
    }

    [Test]
    public async Task ArchiveStream_creates_archive_record()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = Guid.NewGuid().ToString("N");
        await session.Events.StartStream(sid, [new TestEvent("archive-test")]);
        await session.Events.ArchiveStream(sid);
        // Archive doesn't prevent fetching — just marks the stream
    }

    [Test]
    public async Task WriteTombstone_fills_version_gap()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]); // version 1
        await session.Events.WriteTombstone(sid, 3); // skip version 2, write at 3
        var events = await session.Events.FetchStream(sid);
        events.Count.ShouldBe(2);
    }

    [Test]
    public async Task AppendOptimistic_passes_with_correct_version()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sid = Guid.NewGuid().ToString("N");
        await session.Events.Append(sid, [new TestEvent("first")]); // version = 1
        await session.Events.AppendOptimistic(sid, 1, [new TestEvent("second")]); // expected = 1
    }

    // ── AggregateStreamAsync tests ─────────────────────────────────

    [Test]
    public async Task AggregateStreamAsync_ReturnsAggregatedResult()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var streamId = $"agg-{Guid.NewGuid():N}";

        // Append events that WriteModel.Apply handles
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "AGG-1", Amount = 100m }
        ]);

        var result = await session.Events.AggregateStreamAsync<WriteModel>(streamId);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task AggregateStreamAsync_EmptyStream_ReturnsDefault()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var result = await session.Events.AggregateStreamAsync<WriteModel>("nonexistent-stream");
        result.ShouldBeNull();
    }

    // ── Headers tests ──────────────────────────────────────────────

    [Test]
    public async Task Append_WithHeaders_StoresHeaders()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var streamId = $"headers-{Guid.NewGuid():N}";

        var headers = new Dictionary<string, string>
        {
            ["causation_id"] = "cause-123",
            ["correlation_id"] = "corr-456"
        };

        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "HEADERS-1", Amount = 100m }
        ], headers);

        var events = await session.Events.FetchStream(streamId);
        events.Count.ShouldBe(1);
        events[0].Headers.ShouldNotBeNull();
        events[0].Headers!["causation_id"].ShouldBe("cause-123");
        events[0].Headers!["correlation_id"].ShouldBe("corr-456");
    }
}
