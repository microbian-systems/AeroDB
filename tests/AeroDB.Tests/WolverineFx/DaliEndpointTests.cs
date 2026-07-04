namespace AeroDB.Tests;

using System.Reflection;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

/// <summary>
/// Unit tests for <see cref="DaliEndpoint"/>.
/// Uses NSubstitute mocks for the Wolverine runtime and creates real
/// <see cref="DaliMessageStore"/> instances with a mocked <see cref="ISurrealDbClient"/>.
/// </summary>
public class DaliEndpointTests
{
    private static readonly Uri TestUri = new("dali://localhost/test-queue");

    // ─── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a mock <see cref="IWolverineRuntime"/> with a real
    /// <see cref="MessageStoreCollection"/> that contains a <see cref="DaliMessageStore"/>
    /// registered as an ancillary store for <c>typeof(DaliMessageStore)</c>.
    /// This lets <see cref="DaliEndpoint.ResolveStore"/> find the store via
    /// <c>FindAncillaryStore</c>.
    /// </summary>
    private static (IWolverineRuntime Runtime, DaliMessageStore Store) CreateRuntimeWithStore()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var logger = NullLogger<DaliMessageStore>.Instance;
        var store = new DaliMessageStore(client, logger);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

        var ancillaryStore = new AncillaryMessageStore(typeof(DaliMessageStore), store);
        var collection = new MessageStoreCollection(
            runtime,
            Array.Empty<IMessageStore>(),
            new[] { ancillaryStore });

        runtime.Stores.Returns(collection);

        return (runtime, store);
    }

    // ─── Constructor ─────────────────────────────────────────────────

    [Test]
    public void Constructor_SetsDurableMode_AndQueueBrokerRole()
    {
        var endpoint = new DaliEndpoint(TestUri);

        endpoint.Mode.ShouldBe(EndpointMode.Durable);
        endpoint.BrokerRole.ShouldBe("queue");
    }

    [Test]
    public void Constructor_AcceptsUri()
    {
        var endpoint = new DaliEndpoint(TestUri);

        endpoint.Uri.ShouldBe(TestUri);
    }

    // ─── SupportsMode ────────────────────────────────────────────────

    [Test]
    public void SupportsMode_Durable_ReturnsTrue()
    {
        var endpoint = new DaliEndpoint(TestUri);

        endpoint.SupportsMode(EndpointMode.Durable).ShouldBeTrue();
    }

    [Test]
    public void SupportsMode_BufferedInMemory_ReturnsTrue()
    {
        var endpoint = new DaliEndpoint(TestUri);

        endpoint.SupportsMode(EndpointMode.BufferedInMemory).ShouldBeTrue();
    }

    [Test]
    public void SupportsMode_Inline_ReturnsFalse()
    {
        var endpoint = new DaliEndpoint(TestUri);

        endpoint.SupportsMode(EndpointMode.Inline).ShouldBeFalse();
    }

    [Test]
    public void SettingModeToInline_ThrowsInvalidOperationException()
    {
        var endpoint = new DaliEndpoint(TestUri);

        Should.Throw<InvalidOperationException>(() =>
            endpoint.Mode = EndpointMode.Inline);
    }

    // ─── CreateSender ────────────────────────────────────────────────

    [Test]
    public void CreateSender_ReturnsDaliQueueSender()
    {
        var (runtime, _) = CreateRuntimeWithStore();
        var endpoint = new DaliEndpoint(TestUri);

        // CreateSender is protected — invoke via reflection
        var method = typeof(DaliEndpoint).GetMethod("CreateSender",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var sender = (ISender)method!.Invoke(endpoint, [runtime])!;

        sender.ShouldNotBeNull();
        sender.ShouldBeOfType<DaliQueueSender>();
        sender.Destination.ShouldBe(TestUri);
    }

    // ─── BuildListenerAsync ──────────────────────────────────────────

    [Test]
    public async Task BuildListenerAsync_ReturnsStartedDaliQueueListener()
    {
        var (runtime, _) = CreateRuntimeWithStore();
        var endpoint = new DaliEndpoint(TestUri);
        var receiver = Substitute.For<IReceiver>();

        // Pipeline is required when the listener is used, but not during
        // construction/start — set it up to avoid stubbing surprises.
        receiver.Pipeline.Returns(Substitute.For<IHandlerPipeline>());

        var listener = await endpoint.BuildListenerAsync(runtime, receiver);

        listener.ShouldNotBeNull();
        listener.ShouldBeOfType<DaliQueueListener>();
        listener.Address.ShouldBe(TestUri);
    }
}
