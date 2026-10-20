using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Tcp;
using RuntimeWolverineNode = global::Wolverine.Runtime.Agents.WolverineNode;

namespace AeroDB.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBNodeHeartbeatServerTests
{
    [Test]
    public async Task Missing_persisted_node_is_recreated_by_its_existing_owner_heartbeat(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = new AeroDBMessageStore(documentStore.Client,
            NullLogger<AeroDBMessageStore>.Instance, null, documentStore.Options, ownsClient: false);
        await store.InitializeSchemaAsync();

        var options = new WolverineOptions { ApplicationAssembly = typeof(AeroDBNodeHeartbeatServerTests).Assembly };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.Transports.NodeControlEndpoint = new TcpEndpoint("127.0.0.1", AuthenticatedWolverineFixture.GetAvailableLoopbackPort());
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        var node = RuntimeWolverineNode.For(options);
        store.Initialize(runtime);
        var owner = await store.PersistAsync(node, cancellationToken);

        (await documentStore.Client.RawQuery(
            $"DELETE FROM wolverine_nodes:`{node.NodeId}`", cancellationToken: cancellationToken)).EnsureAllOks();

        node.AssignedNodeNumber = owner;
        await store.MarkHealthCheckAsync(node, cancellationToken);

        var recovered = await store.LoadNodeAsync(node.NodeId, cancellationToken);
        recovered.ShouldNotBeNull();
        recovered.AssignedNodeNumber.ShouldBe(owner);
    }
}
