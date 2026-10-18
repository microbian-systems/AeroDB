using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using RuntimeWolverineNode = Wolverine.Runtime.Agents.WolverineNode;
using Wolverine.Transports.Tcp;

namespace AeroDB.Tests;

/// <summary>
/// Durable-node lifecycle regressions. Each test owns a disposable,
/// authenticated coordinator and therefore never depends on the developer's
/// local SurrealDB instance.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBNodeLeaseServerTests
{
    [Test]
    public async Task Renewed_database_lease_blocks_a_peer_until_expiry_then_allows_takeover(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var firstDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var secondDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var first = await CreateInitializedBalancedStoreAsync(firstDocumentStore, cancellationToken);
        await using var second = await CreateInitializedBalancedStoreAsync(secondDocumentStore, cancellationToken);
        var firstOptions = RuntimeOptions();
        var secondOptions = RuntimeOptions();
        var firstNode = RuntimeWolverineNode.For(firstOptions);
        var secondNode = RuntimeWolverineNode.For(secondOptions);

        first.Initialize(RuntimeFor(firstOptions));
        second.Initialize(RuntimeFor(secondOptions));
        await first.PersistAsync(firstNode, cancellationToken);
        await second.PersistAsync(secondNode, cancellationToken);

        (await first.TryAttainLeadershipLockAsync(cancellationToken)).ShouldBeTrue();
        await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        await first.MarkHealthCheckAsync(firstNode, cancellationToken);

        (await second.TryAttainLeadershipLockAsync(cancellationToken)).ShouldBeFalse();
        await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);

        (await second.TryAttainLeadershipLockAsync(cancellationToken)).ShouldBeTrue();
        second.HasLeadershipLock().ShouldBeTrue();
    }

    [Test]
    public async Task Draining_one_node_preserves_another_persisted_nodes_incoming_and_outgoing_ownership(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var drainingDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var peerDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var draining = await CreateInitializedBalancedStoreAsync(drainingDocumentStore, cancellationToken);
        await using var peer = await CreateInitializedBalancedStoreAsync(peerDocumentStore, cancellationToken);
        var drainingOptions = RuntimeOptions();
        var peerOptions = RuntimeOptions();
        var drainingNode = RuntimeWolverineNode.For(drainingOptions);
        var peerNode = RuntimeWolverineNode.For(peerOptions);

        draining.Initialize(RuntimeFor(drainingOptions));
        peer.Initialize(RuntimeFor(peerOptions));
        await draining.PersistAsync(drainingNode, cancellationToken);
        var peerOwner = await peer.PersistAsync(peerNode, cancellationToken);
        var incoming = EnvelopeFor("lease-peer-incoming");
        var outgoing = EnvelopeFor("lease-peer-outgoing");
        await peer.StoreIncomingAsync(incoming);
        await peer.StoreOutgoingAsync(outgoing, peerOwner);

        await draining.DrainAsync();

        (await OwnerIdAsync(peerDocumentStore, "wolverine_incoming_envelopes", incoming.Id, cancellationToken))
            .ShouldBe(peerOwner);
        (await OwnerIdAsync(peerDocumentStore, "wolverine_outgoing_envelopes", outgoing.Id, cancellationToken))
            .ShouldBe(peerOwner);
    }

    private static async Task<AeroDBMessageStore> CreateInitializedBalancedStoreAsync(
        IDocumentStore documentStore,
        CancellationToken cancellationToken)
    {
        var store = new AeroDBMessageStore(
            documentStore.Client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: documentStore.Options,
            ownsClient: false);
        await store.InitializeSchemaAsync();
        return store;
    }

    private static WolverineOptions RuntimeOptions()
    {
        var options = new WolverineOptions
        {
            ApplicationAssembly = typeof(AeroDBNodeLeaseServerTests).Assembly
        };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(100);
        options.Durability.StaleNodeTimeout = TimeSpan.FromMilliseconds(700);
        options.Transports.NodeControlEndpoint = new TcpEndpoint(
            "127.0.0.1",
            AuthenticatedWolverineFixture.GetAvailableLoopbackPort());
        return options;
    }

    private static IWolverineRuntime RuntimeFor(WolverineOptions options)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        return runtime;
    }

    private static Envelope EnvelopeFor(string messageType) => new()
    {
        Id = Guid.NewGuid(),
        Destination = new Uri("loopback://node-lease"),
        MessageType = messageType,
        Data = "node-lease"u8.ToArray()
    };

    private static async Task<int> OwnerIdAsync(
        IDocumentStore documentStore,
        string table,
        Guid envelopeId,
        CancellationToken cancellationToken)
    {
        var response = await documentStore.Client.RawQuery(
            $"SELECT VALUE owner_id FROM {table}:`{envelopeId:D}`;",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        var owners = response.GetValue<List<int>>(0)
            ?? throw new InvalidOperationException("The ownership read did not return a result set.");
        return owners.ShouldHaveSingleItem();
    }
}
