namespace AeroDB.Tests;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using TUnit.Core;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Transports;

/// <summary>
/// Integration tests for AeroDBQueueListener using file-based SurrealKV.
/// The polling loop is tested structurally (start/stop, empty queue).
/// Full end-to-end dispatch requires a persistent SurrealDB instance
/// and is covered by AeroDBWolverineIntegrationTests (Solo mode).
/// </summary>
public class AeroDBQueueListenerTests
{
    private static readonly Uri TestUri = new("aerodb://localhost/test-queue");

    private static async Task<AeroDBMessageStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"AeroDB_listener_test_{Guid.NewGuid():N}.db");
        var client = new SurrealDbKvClient(dbPath);
        await client.Use("test", "test");
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);
        await store.InitializeSchemaAsync();
        return store;
    }

    [Test]
    public async Task Listener_StartStop_CycleWorks()
    {
        var store = await CreateStoreAsync();
        try
        {
            var receiver = Substitute.For<IReceiver>();
            receiver.Pipeline.Returns(Substitute.For<IHandlerPipeline>());

            var options = new AeroDBTransportOptions { PollingInterval = TimeSpan.FromMilliseconds(100) };
            var listener = new AeroDBQueueListener(store, receiver, options,
                NullLogger<AeroDBQueueListener>.Instance, TestUri);

            // Start → poll a bit → stop
            await listener.StartAsync();
            await Task.Delay(500);
            await listener.StopAsync();

            // Can restart and stop again
            await listener.StartAsync();
            await Task.Delay(500);
            await listener.StopAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Test]
    public async Task Listener_NoMessages_DoesNotDispatch()
    {
        var store = await CreateStoreAsync();
        try
        {
            var receiver = Substitute.For<IReceiver>();
            receiver.Pipeline.Returns(Substitute.For<IHandlerPipeline>());

            var options = new AeroDBTransportOptions { PollingInterval = TimeSpan.FromMilliseconds(100) };
            var listener = new AeroDBQueueListener(store, receiver, options,
                NullLogger<AeroDBQueueListener>.Instance, TestUri);
            await listener.StartAsync();

            await Task.Delay(500);

            await receiver.DidNotReceiveWithAnyArgs().ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope[]>());

            await listener.DisposeAsync();
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    [Test]
    public async Task Listener_DisposeAsync_StopsGracefully()
    {
        var store = await CreateStoreAsync();
        try
        {
            var receiver = Substitute.For<IReceiver>();
            receiver.Pipeline.Returns(Substitute.For<IHandlerPipeline>());

            var options = new AeroDBTransportOptions { PollingInterval = TimeSpan.FromMilliseconds(100) };
            var listener = new AeroDBQueueListener(store, receiver, options,
                NullLogger<AeroDBQueueListener>.Instance, TestUri);
            await listener.StartAsync();

            await listener.DisposeAsync();
            // No exception = success
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
}
