using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net.Exceptions.Response;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using WolverineNode = Wolverine.Runtime.Agents.WolverineNode;
using Wolverine.Transports.Tcp;

namespace AeroDB.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBNodeCoordinationServerTests
{
    [Test]
    public async Task Concurrent_balanced_nodes_receive_distinct_database_assigned_owner_numbers_and_preserve_identity(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var firstDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var secondDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var first = await CreateMessageStoreAsync(firstDocumentStore, cancellationToken);
        await using var second = await CreateMessageStoreAsync(secondDocumentStore, cancellationToken);
        var firstNode = WolverineNode.For(DefaultBalancedOptions());
        var secondNode = WolverineNode.For(DefaultBalancedOptions());

        firstNode.AssignedNodeNumber.ShouldBe(1);
        secondNode.AssignedNodeNumber.ShouldBe(1);

        int[] assigned;
        try
        {
            assigned = await Task.WhenAll(
                first.PersistAsync(firstNode, cancellationToken),
                second.PersistAsync(secondNode, cancellationToken));
        }
        catch (ResponseUnsuccessfulException exception)
        {
            // This disposable authenticated fixture is synthetic. Preserve the
            // server's exact SurrealQL/schema diagnostic for the RED receipt;
            // production code deliberately does not log response contents.
            throw new InvalidOperationException($"Synthetic node-allocation response: {exception.Message}", exception);
        }

        assigned.ShouldAllBe(number => number > 0);
        assigned[0].ShouldNotBe(assigned[1]);

        var persisted = await first.LoadAllNodesAsync(cancellationToken);
        persisted.Single(node => node.NodeId == firstNode.NodeId).AssignedNodeNumber.ShouldBe(assigned[0]);
        persisted.Single(node => node.NodeId == secondNode.NodeId).AssignedNodeNumber.ShouldBe(assigned[1]);
    }

    [Test]
    public async Task Node_bound_leadership_is_exclusive_and_non_owner_release_cannot_delete_the_winner(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var firstDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var secondDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var contenderDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var first = await CreateMessageStoreAsync(firstDocumentStore, cancellationToken);
        await using var second = await CreateMessageStoreAsync(secondDocumentStore, cancellationToken);
        await using var contender = await CreateMessageStoreAsync(contenderDocumentStore, cancellationToken);

        var firstOptions = DefaultBalancedOptions();
        var secondOptions = DefaultBalancedOptions();
        var contenderOptions = DefaultBalancedOptions();
        var firstNode = WolverineNode.For(firstOptions);
        var secondNode = WolverineNode.For(secondOptions);
        var contenderNode = WolverineNode.For(contenderOptions);
        first.Initialize(RuntimeFor(firstOptions));
        second.Initialize(RuntimeFor(secondOptions));
        contender.Initialize(RuntimeFor(contenderOptions));

        await first.PersistAsync(firstNode, cancellationToken);
        await second.PersistAsync(secondNode, cancellationToken);
        await contender.PersistAsync(contenderNode, cancellationToken);

        var acquired = await Task.WhenAll(
            first.TryAttainLeadershipLockAsync(cancellationToken),
            second.TryAttainLeadershipLockAsync(cancellationToken));

        acquired.Count(value => value).ShouldBe(1);
        var winner = acquired[0] ? first : second;
        var nonOwner = acquired[0] ? second : first;
        winner.HasLeadershipLock().ShouldBeTrue();
        nonOwner.HasLeadershipLock().ShouldBeFalse();

        await nonOwner.ReleaseLeadershipLockAsync();

        (await contender.TryAttainLeadershipLockAsync(cancellationToken)).ShouldBeFalse();
        contender.HasLeadershipLock().ShouldBeFalse();

        await winner.ReleaseLeadershipLockAsync();
    }

    [Test]
    public async Task Leadership_fails_closed_when_no_runtime_has_supplied_the_existing_durability_lease_settings(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        await store.PersistAsync(WolverineNode.For(DefaultBalancedOptions()), cancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => { await store.TryAttainLeadershipLockAsync(cancellationToken); });
        store.HasLeadershipLock().ShouldBeFalse();
    }

    [Test]
    public async Task Repeated_persist_of_the_same_node_reconciles_its_original_database_owner(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var node = WolverineNode.For(DefaultBalancedOptions());

        var first = await store.PersistAsync(node, cancellationToken);
        var second = await store.PersistAsync(node, cancellationToken);

        second.ShouldBe(first);
        (await store.LoadAllNodesAsync(cancellationToken)).Count.ShouldBe(1);
    }

    [Test]
    public async Task Bound_balanced_store_rejects_owner_resolution_before_database_persist(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var options = DefaultBalancedOptions();
        store.Initialize(RuntimeFor(options));

        Should.Throw<InvalidOperationException>(() => store.GetOwnerId());
    }

    [Test]
    public async Task Bound_runtime_rejects_a_foreign_node_before_any_node_row_or_sequence_is_written(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var boundOptions = DefaultBalancedOptions();
        store.Initialize(RuntimeFor(boundOptions));
        var foreignNode = WolverineNode.For(DefaultBalancedOptions());

        await Should.ThrowAsync<InvalidOperationException>(
            async () => { await store.PersistAsync(foreignNode, cancellationToken); });
        (await store.LoadAllNodesAsync(cancellationToken)).ShouldBeEmpty();
        var sequence = await documentStore.Client.RawQuery("SELECT * FROM wolverine_node_sequence", cancellationToken: cancellationToken);
        sequence.EnsureAllOks();
        (sequence.GetValue<List<WolverineNodeSequence>>(0) ?? new List<WolverineNodeSequence>()).ShouldBeEmpty();
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
        cancellationToken.ThrowIfCancellationRequested();
        return store;
    }

    private static WolverineOptions DefaultBalancedOptions()
    {
        var options = new WolverineOptions
        {
            ApplicationAssembly = typeof(AeroDBNodeCoordinationServerTests).Assembly
        };
        options.Durability.Mode = DurabilityMode.Balanced;
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
