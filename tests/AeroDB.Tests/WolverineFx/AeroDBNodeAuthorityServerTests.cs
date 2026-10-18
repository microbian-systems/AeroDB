using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;
using Wolverine.Runtime.Agents;

namespace AeroDB.Tests;

/// <summary>
/// Regression coverage for the persisted data which decides Wolverine node
/// authority. These checks intentionally exercise a real authenticated
/// SurrealDB instance rather than an in-memory client.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBNodeAuthorityServerTests
{
    [Test]
    public async Task Persisted_agent_restrictions_round_trip_pinned_paused_and_none_with_their_exact_authority_values(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var expected = new[]
        {
            new AgentRestriction(
                Guid.NewGuid(),
                new Uri("wolverine://authority/contract-review"),
                AgentRestrictionType.Pinned,
                41),
            new AgentRestriction(
                Guid.NewGuid(),
                new Uri("wolverine://authority/paused-review"),
                AgentRestrictionType.Paused,
                0),
            new AgentRestriction(
                Guid.NewGuid(),
                new Uri("wolverine://authority/unrestricted-review"),
                AgentRestrictionType.None,
                0)
        };

        await store.PersistAgentRestrictionsAsync(expected, cancellationToken);

        var state = await store.LoadNodeAgentStateAsync(cancellationToken);
        state.Restrictions.Current.Count.ShouldBe(expected.Length);
        foreach (var expectedRestriction in expected)
        {
            var actual = state.Restrictions.Current.Single(restriction => restriction.Id == expectedRestriction.Id);
            actual.AgentUri.ShouldBe(expectedRestriction.AgentUri);
            actual.Type.ShouldBe(expectedRestriction.Type);
            actual.NodeNumber.ShouldBe(expectedRestriction.NodeNumber);
        }
    }

    [Test]
    public async Task Load_node_agent_state_rejects_a_malformed_restriction_instead_of_silently_skipping_it(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);

        (await documentStore.Client.RawQuery(
            "CREATE wolverine_agent_restrictions CONTENT { agent_uri: 'not a URI', type: 'Pinned', node_number: 3 };",
            cancellationToken: cancellationToken)).EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadNodeAgentStateAsync(cancellationToken));
    }

    [Test]
    public async Task Load_node_agent_state_rejects_an_undefined_numeric_restriction_type(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);

        (await documentStore.Client.RawQuery(
            "CREATE wolverine_agent_restrictions CONTENT { agent_uri: 'wolverine://authority/unknown', type: '999', node_number: 0 };",
            cancellationToken: cancellationToken)).EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadNodeAgentStateAsync(cancellationToken));
    }

    [Test]
    public async Task Load_node_honors_a_pre_cancelled_token_before_querying_authority_data(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => store.LoadNodeAsync(Guid.NewGuid(), cancelled.Token));
    }

    [Test]
    public async Task Load_node_returns_a_valid_persisted_authority_record(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var nodeId = Guid.NewGuid();

        await SeedNodeAsync(documentStore, nodeId.ToString("D"), 12, cancellationToken);

        var node = await store.LoadNodeAsync(nodeId, cancellationToken);
        node.ShouldNotBeNull();
        node.NodeId.ShouldBe(nodeId);
        node.AssignedNodeNumber.ShouldBe(12);
    }

    [Test]
    public async Task Load_node_rejects_a_persisted_non_positive_node_number_instead_of_returning_authority(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var nodeId = Guid.NewGuid();

        await SeedNodeAsync(documentStore, nodeId.ToString("D"), 0, cancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadNodeAsync(nodeId, cancellationToken));
    }

    [Test]
    public async Task Load_node_rejects_a_persisted_node_without_a_health_check_timestamp(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var nodeId = Guid.NewGuid();

        await SeedNodeAsync(documentStore, nodeId.ToString("D"), 9, cancellationToken, includeHealthCheck: false);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadNodeAsync(nodeId, cancellationToken));
    }

    [Test]
    public async Task Load_node_rejects_a_persisted_node_with_a_null_health_check_timestamp(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);
        var nodeId = Guid.NewGuid();

        await SeedNodeAsync(documentStore, nodeId.ToString("D"), 10, cancellationToken,
            healthCheckIsNull: true);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadNodeAsync(nodeId, cancellationToken));
    }

    [Test]
    public async Task Load_all_nodes_rejects_a_malformed_persisted_node_identity_instead_of_silently_skipping_it(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var documentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var store = await CreateMessageStoreAsync(documentStore, cancellationToken);

        await SeedNodeAsync(documentStore, "not-a-guid", 8, cancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.LoadAllNodesAsync(cancellationToken));
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

    private static async Task SeedNodeAsync(
        IDocumentStore documentStore,
        string nodeId,
        int nodeNumber,
        CancellationToken cancellationToken,
        bool includeHealthCheck = true,
        DateTimeOffset? healthCheckTime = null,
        bool healthCheckIsNull = false)
    {
        var content = new Dictionary<string, object?>
        {
            ["node_number"] = nodeNumber,
            ["description"] = "authority-test-node",
            ["started"] = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)
        };
        if (includeHealthCheck)
            content["health_check_time"] = healthCheckIsNull ? null : healthCheckTime ??
                new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var response = await documentStore.Client.RawQuery(
            "CREATE type::record(\"wolverine_nodes\", $nodeId) CONTENT $content;",
            new Dictionary<string, object?>
            {
                ["nodeId"] = nodeId,
                ["content"] = content
            },
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
    }
}
