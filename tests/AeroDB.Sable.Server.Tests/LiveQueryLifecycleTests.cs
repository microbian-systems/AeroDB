using AeroDB.Sable.LiveQuery;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class LiveQueryLifecycleTests
{
    [Test]
    public async Task DisposeAsync_delivers_close_before_completing(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            $"live_lifecycle_{Guid.NewGuid():N}",
            options => options.Schema.For<LiveDocument>().SetSchemaMode(SchemaMode.Flexible), ct);
        await using var session = await store.LiveQuerySessionAsync(ct);
        var query = await session.Live<LiveDocument>().SubscribeAsync(ct);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<AeroDBLiveAction>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var change in query.Changes(ct))
            {
                changes.Add(change.Action);
                if (change.Action == AeroDBLiveAction.Open) opened.TrySetResult();
            }
        }, ct);

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await query.DisposeAsync();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5), ct);

        changes.ShouldBe(new[] { AeroDBLiveAction.Open, AeroDBLiveAction.Closed });
    }

    public sealed class LiveDocument
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [Test]
    public async Task DisposeSession_completes_owned_subscriptions(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            $"live_session_lifecycle_{Guid.NewGuid():N}",
            options => options.Schema.For<LiveDocument>().SetSchemaMode(SchemaMode.Flexible), ct);
        var session = await store.LiveQuerySessionAsync(ct);
        var query = await session.Live<LiveDocument>().SubscribeAsync(ct);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new List<AeroDBLiveAction>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var change in query.Changes(ct))
            {
                changes.Add(change.Action);
                if (change.Action == AeroDBLiveAction.Open) opened.TrySetResult();
            }
        }, ct);

        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await session.DisposeAsync();
            await consumer.WaitAsync(TimeSpan.FromSeconds(2), ct);
            changes.ShouldBe(new[] { AeroDBLiveAction.Open, AeroDBLiveAction.Closed });
            Should.Throw<ObjectDisposedException>(() => session.Live<LiveDocument>());
        }
        finally
        {
            await query.DisposeAsync();
            await session.DisposeAsync();
            await consumer;
        }
    }
}
