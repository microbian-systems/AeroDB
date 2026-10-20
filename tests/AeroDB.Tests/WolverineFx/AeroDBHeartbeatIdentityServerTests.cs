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
/// Wolverine's heartbeat controller creates a fresh node instance from the
/// runtime options. In Balanced mode that instance starts at Wolverine's
/// provisional owner number, not AeroDB's persisted owner number.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBHeartbeatIdentityServerTests
{
    [Test]
    public async Task Fresh_runtime_node_heartbeat_uses_the_persisted_database_owner_not_wolverines_provisional_owner(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var setupDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var mainDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var setupStore = await CreateMessageStoreAsync(setupDocumentStore, cancellationToken);
        var setupOptions = RuntimeOptions();
        setupStore.Initialize(RuntimeFor(setupOptions));
        var firstOwner = await setupStore.PersistAsync(RuntimeWolverineNode.For(setupOptions), cancellationToken);

        await using var mainStore = await CreateMessageStoreAsync(mainDocumentStore, cancellationToken);
        var mainOptions = RuntimeOptions();
        var mainRuntime = RuntimeFor(mainOptions);
        var persistedNode = RuntimeWolverineNode.For(mainOptions);
        mainStore.Initialize(mainRuntime);
        var persistedOwner = await mainStore.PersistAsync(persistedNode, cancellationToken);
        persistedOwner.ShouldBeGreaterThan(firstOwner);
        persistedOwner.ShouldBeGreaterThan(1);

        var freshHeartbeatNode = RuntimeWolverineNode.For(mainOptions);
        freshHeartbeatNode.NodeId.ShouldBe(persistedNode.NodeId);
        freshHeartbeatNode.AssignedNodeNumber.ShouldBe(1);

        await mainStore.MarkHealthCheckAsync(freshHeartbeatNode, cancellationToken);

        var persisted = await mainStore.LoadNodeAsync(persistedNode.NodeId, cancellationToken);
        persisted.ShouldNotBeNull();
        persisted.AssignedNodeNumber.ShouldBe(persistedOwner);
        persisted.LastHealthCheck.ShouldNotBe(default);
    }

    [Test]
    public async Task Fresh_runtime_node_heartbeat_recreates_a_deleted_row_with_the_persisted_database_owner(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var setupDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var mainDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var setupStore = await CreateMessageStoreAsync(setupDocumentStore, cancellationToken);
        var setupOptions = RuntimeOptions();
        setupStore.Initialize(RuntimeFor(setupOptions));
        await setupStore.PersistAsync(RuntimeWolverineNode.For(setupOptions), cancellationToken);

        await using var mainStore = await CreateMessageStoreAsync(mainDocumentStore, cancellationToken);
        var mainOptions = RuntimeOptions();
        var mainRuntime = RuntimeFor(mainOptions);
        var persistedNode = RuntimeWolverineNode.For(mainOptions);
        mainStore.Initialize(mainRuntime);
        var persistedOwner = await mainStore.PersistAsync(persistedNode, cancellationToken);
        persistedOwner.ShouldBeGreaterThan(1);

        (await mainDocumentStore.Client.RawQuery(
            $"DELETE FROM wolverine_nodes:`{persistedNode.NodeId:D}`;",
            cancellationToken: cancellationToken)).EnsureAllOks();

        var freshHeartbeatNode = RuntimeWolverineNode.For(mainOptions);
        freshHeartbeatNode.AssignedNodeNumber.ShouldBe(1);
        await mainStore.MarkHealthCheckAsync(freshHeartbeatNode, cancellationToken);

        var recreated = await mainStore.LoadNodeAsync(persistedNode.NodeId, cancellationToken);
        recreated.ShouldNotBeNull();
        recreated.AssignedNodeNumber.ShouldBe(persistedOwner);
        recreated.LastHealthCheck.ShouldNotBe(default);
    }

    private static async Task<AeroDBMessageStore> CreateMessageStoreAsync(
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
            ApplicationAssembly = typeof(AeroDBHeartbeatIdentityServerTests).Assembly
        };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(100);
        options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(1);
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
}
