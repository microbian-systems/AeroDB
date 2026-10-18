using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using JasperFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using TUnit.Core;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using RuntimeNode = Wolverine.Runtime.Agents.WolverineNode;

namespace AeroDB.Tests;

/// <summary>
/// Contract tests for the dormant tenant-only durability wrapper. These tests
/// deliberately do not register the wrapper with Wolverine; discovery and
/// recovery activation remain a separate, explicitly gated change.
/// </summary>
public sealed class AeroDBDurabilityAgentTests
{
    [Test]
    public void Raw_single_store_remains_without_agent_support_while_tenant_wrapper_has_stable_identity()
    {
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var raw = RawStore(location);

        raw.ShouldNotBeAssignableTo<IMessageStoreWithAgentSupport>();

        var wrapper = new AeroDBTenantMessageStore(location, raw);

        wrapper.ShouldBeAssignableTo<IMessageStoreWithAgentSupport>();
        wrapper.Uri.ShouldBe(location.PhysicalIdentity);
        wrapper.Role.ShouldBe(MessageStoreRole.Tenant);
        wrapper.TenantIds.ShouldBe(["alpha"]);
        wrapper.Inbox.ShouldBeSameAs(raw.Inbox);
        wrapper.Outbox.ShouldBeSameAs(raw.Outbox);
        wrapper.Admin.ShouldBeSameAs(raw.Admin);
        wrapper.Nodes.ShouldBeSameAs(raw.Nodes);
    }

    [Test]
    public void Wrapper_builds_one_inert_agent_for_its_physical_store()
    {
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var wrapper = new AeroDBTenantMessageStore(location, RawStore(location));
        var runtime = Substitute.For<IWolverineRuntime>();

        var first = wrapper.BuildAgent(runtime);
        var second = wrapper.BuildAgent(runtime);

        first.ShouldBeSameAs(second);
        first.Uri.ShouldBe(location.PhysicalIdentity);
        first.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Test]
    public async Task Agent_start_is_idempotent_and_stop_cancels_and_awaits_its_single_loop()
    {
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var agent = new AeroDBDurabilityAgent(location.PhysicalIdentity,
            async cancellationToken =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });

        await agent.StartAsync(CancellationToken.None);
        await agent.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runs.ShouldBe(1);
        agent.Status.ShouldBe(AgentStatus.Running);
        release.TrySetResult();
        await agent.StopAsync(CancellationToken.None);

        agent.Status.ShouldBe(AgentStatus.Stopped);
        runs.ShouldBe(1);
    }

    [Test]
    public async Task Start_during_stop_cannot_create_a_second_loop_or_report_stopped_early()
    {
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var agent = new AeroDBDurabilityAgent(location.PhysicalIdentity,
            async cancellationToken =>
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await releaseStop.Task;
                    throw;
                }
            });

        await agent.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = agent.StopAsync(CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await agent.StartAsync(CancellationToken.None);
        agent.Status.ShouldBe(AgentStatus.Running);
        runs.ShouldBe(1);

        releaseStop.TrySetResult();
        await stopping;
        agent.Status.ShouldBe(AgentStatus.Stopped);
        runs.ShouldBe(1);
    }

    [Test]
    public async Task Stop_after_a_naturally_completed_generation_cancels_and_drains_the_next_generation()
    {
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var firstRunCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var agent = new AeroDBDurabilityAgent(location.PhysicalIdentity,
            async cancellationToken =>
            {
                var run = Interlocked.Increment(ref runs);
                if (run == 1)
                {
                    firstRunCompleted.TrySetResult();
                    return;
                }

                if (run == 2)
                    secondRunEntered.TrySetResult();
                else
                    thirdRunEntered.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (run == 2)
                        secondRunCancelled.TrySetResult();

                    throw;
                }
            });

        await agent.StartAsync(CancellationToken.None);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.StopAsync(CancellationToken.None);

        await agent.StartAsync(CancellationToken.None);
        await secondRunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.StopAsync(CancellationToken.None);
        await secondRunCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        agent.Status.ShouldBe(AgentStatus.Stopped);

        try
        {
            await agent.StartAsync(CancellationToken.None);
            await thirdRunEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            agent.Status.ShouldBe(AgentStatus.Running);
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }

    // These are retrospective recovery tests for the dormant tenant wrapper.
    // They deliberately leave listener delivery unconfigured: owner-zero work is
    // discovered, but this suite makes no claim that a null listener delivered it.
    [Test]
    public async Task Untrusted_empty_authority_keeps_stale_ownership_and_reports_unhealthy()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([]);
        var stale = await fixture.StoreIncomingAsync(ownerId: 41);
        var globallyOwned = await fixture.StoreIncomingAsync(ownerId: 0);

        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(stale)).ShouldBe(41);
        (await fixture.OwnerIdAsync(globallyOwned)).ShouldBe(0);
        fixture.Endpoints.Received(1).FindListenerCircuit(new Uri("loopback://tenant-recovery"));
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Test]
    public async Task Owner_zero_incoming_metadata_preserves_the_listener_and_count()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([]);
        await fixture.StoreIncomingAsync(ownerId: 0);

        var counts = await fixture.Store.FindGloballyOwnedIncomingCountsAsync(CancellationToken.None);

        counts.Count.ShouldBe(1);
        counts[0].Destination.ShouldBe(new Uri("loopback://tenant-recovery"));
        counts[0].Count.ShouldBe(1);
    }

    [Test]
    public async Task Owner_zero_outgoing_metadata_preserves_the_destination()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([]);
        await fixture.StoreOutgoingAsync(ownerId: 0);

        var destinations = await fixture.Store.FindGloballyOwnedOutgoingDestinationsAsync(CancellationToken.None);

        destinations.Count.ShouldBe(1);
        destinations[0].ShouldBe(new Uri("loopback://tenant-recovery-outgoing"));
    }

    [Test]
    public async Task Incoming_metadata_rejects_missing_invalid_and_overflowed_projection_fields()
    {
        await using var responses = new SurrealDbMemoryClient();
        await responses.Use("ca_alpha", "main");
        foreach (var query in new[]
        {
            "RETURN NONE;", "RETURN [NONE];", "RETURN [{}];",
            "RETURN [{ received_at: 'loopback://tenant-recovery', total: NONE }];",
            "RETURN [{ received_at: 'loopback://tenant-recovery', total: 0 }];",
            "RETURN [{ received_at: 'loopback://tenant-recovery', total: -1 }];",
            "RETURN [{ received_at: 'loopback://tenant-recovery', total: 2147483648 }];",
            "RETURN [{ received_at: 'not-an-absolute-uri', total: 1 }];"
        })
        {
            var response = await responses.RawQuery(query);
            response.EnsureAllOks();
            var client = Substitute.For<ISurrealDbClient>();
            client.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(response));
            var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

            await Should.ThrowAsync<Exception>(() => store.FindGloballyOwnedIncomingCountsAsync(CancellationToken.None));
        }
    }

    [Test]
    public async Task Outgoing_metadata_rejects_missing_and_invalid_destinations()
    {
        await using var responses = new SurrealDbMemoryClient();
        await responses.Use("ca_alpha", "main");
        foreach (var query in new[]
        {
            "RETURN NONE;", "RETURN [NONE];", "RETURN [{}];",
            "RETURN [{ destination: NONE }];", "RETURN [{ destination: 'not-an-absolute-uri' }];"
        })
        {
            var response = await responses.RawQuery(query);
            response.EnsureAllOks();
            var client = Substitute.For<ISurrealDbClient>();
            client.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(response));
            var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

            await Should.ThrowAsync<Exception>(() => store.FindGloballyOwnedOutgoingDestinationsAsync(CancellationToken.None));
        }
    }

    [Test]
    public async Task Trusted_authority_releases_absent_owner_and_reports_healthy()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([Node(7)]);
        var stale = await fixture.StoreIncomingAsync(ownerId: 41);
        var globallyOwned = await fixture.StoreIncomingAsync(ownerId: 0);

        (await fixture.Store.FindDistinctOwnerIdsAsync(CancellationToken.None)).ShouldContain(41);
        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(stale)).ShouldBe(0);
        (await fixture.OwnerIdAsync(globallyOwned)).ShouldBe(0);
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    [Test]
    public async Task Current_assigned_owner_is_preserved_when_authority_omits_it_and_health_is_healthy()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([Node(41)]);
        var active = await fixture.StoreIncomingAsync(ownerId: fixture.AssignedNodeNumber);

        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(active)).ShouldBe(fixture.AssignedNodeNumber);
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    [Test]
    public async Task Throwing_or_corrupt_authority_never_releases_stale_ownership_and_is_unhealthy()
    {
        var authority = Substitute.For<INodeAgentPersistence>();
        await using var fixture = await TenantRecoveryFixture.CreateAsync(authority);
        var stale = await fixture.StoreIncomingAsync(ownerId: 41);
        await fixture.StoreIncomingAsync(ownerId: 0);

        foreach (var corruptAuthority in new IReadOnlyList<RuntimeNode>[] { [Node(0)], [Node(9), Node(9)] })
        {
            authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(corruptAuthority));
            await fixture.Agent.RecoverOnceAsync(CancellationToken.None);
            (await fixture.OwnerIdAsync(stale)).ShouldBe(41);
            fixture.Endpoints.Received().FindListenerCircuit(new Uri("loopback://tenant-recovery"));
            fixture.Endpoints.ClearReceivedCalls();
            (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
                .Status.ShouldBe(HealthStatus.Unhealthy);
        }

        authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<RuntimeNode>>(new InvalidOperationException("authority unavailable")));
        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(stale)).ShouldBe(41);
        fixture.Endpoints.Received(1).FindListenerCircuit(new Uri("loopback://tenant-recovery"));
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Test]
    public async Task Owner_metadata_reads_the_union_of_both_tables_and_handles_empty_tables()
    {
        await using var fixture = await TenantRecoveryFixture.CreateAsync([Node(7)]);
        (await fixture.Store.FindDistinctOwnerIdsAsync(CancellationToken.None)).ShouldBeEmpty();

        await fixture.StoreIncomingAsync(ownerId: 41);
        await fixture.StoreOutgoingAsync(ownerId: 77);

        var owners = await fixture.Store.FindDistinctOwnerIdsAsync(CancellationToken.None);
        owners.ShouldContain(41);
        owners.ShouldContain(77);
        owners.Count.ShouldBe(2);
    }

    [Test]
    public async Task Owner_metadata_second_read_failure_cannot_return_a_partial_owner_set()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var first = new SurrealDbResponse([Substitute.For<ISurrealDbResult>()]);
        var second = new SurrealDbResponse([Substitute.For<ISurrealDbErrorResult>()]);
        client.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(first), Task.FromResult(second));
        var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null, storeOptions: null, ownsClient: false, storeIdentity: location.PhysicalIdentity);

        await Should.ThrowAsync<SurrealDb.Net.Exceptions.Response.ResponseUnsuccessfulException>(
            () => store.FindDistinctOwnerIdsAsync(CancellationToken.None));
    }

    [Test]
    public async Task Owner_metadata_rejects_malformed_projection_responses_without_returning_partial_owners()
    {
        await using var responses = new SurrealDbMemoryClient();
        await responses.Use("ca_alpha", "main");
        var valid = await responses.RawQuery("RETURN [{ owner_id: 41 }];");
        foreach (var malformedQuery in new[] { "RETURN NONE;", "RETURN [{}];", "RETURN [{ owner_id: NONE }];", "RETURN [{ owner_id: 0 }];", "RETURN [{ owner_id: -1 }];" })
        {
            var client = Substitute.For<ISurrealDbClient>();
            var malformed = await responses.RawQuery(malformedQuery);
            client.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(valid), Task.FromResult(malformed));
            var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

            await Should.ThrowAsync<Exception>(() => store.FindDistinctOwnerIdsAsync(CancellationToken.None));
        }
    }

    [Test]
    public async Task Authority_active_and_current_owners_survive_while_only_stale_ownership_is_released()
    {
        var authority = Substitute.For<INodeAgentPersistence>();
        await using var fixture = await TenantRecoveryFixture.CreateAsync(authority);
        authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RuntimeNode>>([Node(77), Node(fixture.AssignedNodeNumber)]));
        var stale = await fixture.StoreIncomingAsync(ownerId: 41);
        var active = await fixture.StoreIncomingAsync(ownerId: 77);
        var current = await fixture.StoreIncomingAsync(ownerId: fixture.AssignedNodeNumber);

        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(stale)).ShouldBe(0);
        (await fixture.OwnerIdAsync(active)).ShouldBe(77);
        (await fixture.OwnerIdAsync(current)).ShouldBe(fixture.AssignedNodeNumber);
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    [Test]
    public async Task Owner_metadata_preserves_original_cancellation_after_a_noncooperative_second_read()
    {
        await using var responses = new SurrealDbMemoryClient();
        await responses.Use("ca_alpha", "main");
        var valid = await responses.RawQuery("RETURN [{ owner_id: 41 }];");
        var client = Substitute.For<ISurrealDbClient>();
        using var cancelled = new CancellationTokenSource();
        var reads = 0;
        client.RawQuery(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ShouldBe(cancelled.Token);
                if (++reads == 2) cancelled.Cancel();
                return Task.FromResult(valid);
            });
        await using var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null, storeOptions: null, ownsClient: false);

        var exception = await Should.ThrowAsync<OperationCanceledException>(
            () => store.FindDistinctOwnerIdsAsync(cancelled.Token));

        exception.CancellationToken.ShouldBe(cancelled.Token);
        reads.ShouldBe(2);
    }

    [Test]
    public async Task Valid_authority_after_bad_tick_releases_stale_owner_and_clears_health()
    {
        var authority = Substitute.For<INodeAgentPersistence>();
        authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RuntimeNode>>([]));

        await using var fixture = await TenantRecoveryFixture.CreateAsync(authority);
        var stale = await fixture.StoreIncomingAsync(ownerId: 41);

        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);
        (await fixture.OwnerIdAsync(stale)).ShouldBe(41);
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Unhealthy);

        authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RuntimeNode>>([Node(7)]));

        await fixture.Agent.RecoverOnceAsync(CancellationToken.None);

        (await fixture.OwnerIdAsync(stale)).ShouldBe(0);
        (await fixture.Agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    private static RuntimeNode Node(int ownerId) => new() { AssignedNodeNumber = ownerId };

    private sealed class OwnerRow
    {
        public int owner_id { get; set; }
    }

    private sealed class TenantRecoveryFixture : IAsyncDisposable
    {
        private readonly SurrealDbMemoryClient _client;
        private readonly IDocumentStore _documents;

        private TenantRecoveryFixture(
            SurrealDbMemoryClient client,
            IDocumentStore documents,
            AeroDBMessageStore store,
            AeroDBDurabilityAgent agent,
            IEndpointCollection endpoints,
            int assignedNodeNumber)
        {
            _client = client;
            _documents = documents;
            Store = store;
            Agent = agent;
            Endpoints = endpoints;
            AssignedNodeNumber = assignedNodeNumber;
        }

        public AeroDBMessageStore Store { get; }
        public AeroDBDurabilityAgent Agent { get; }
        public IEndpointCollection Endpoints { get; }
        public int AssignedNodeNumber { get; }

        public static Task<TenantRecoveryFixture> CreateAsync(IReadOnlyList<RuntimeNode> nodes)
        {
            var authority = Substitute.For<INodeAgentPersistence>();
            authority.LoadAllNodesAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(nodes));
            return CreateAsync(authority);
        }

        public static async Task<TenantRecoveryFixture> CreateAsync(INodeAgentPersistence authority)
        {
            var client = new SurrealDbMemoryClient();
            await client.Use("ca_alpha", "main");
            var options = new StoreOptions
            {
                Namespace = "ca_alpha",
                Database = "main"
            };
            var documents = Documents.For(o =>
            {
                o.ClientFactory = () => client;
                o.Namespace = options.Namespace;
                o.Database = options.Database;
            });
            await documents.InitializeAsync();

            var location = new AeroDBTenantStoreLocation("alpha", "primary", "ca_alpha", "main");
            var store = new AeroDBMessageStore(
                client,
                NullLogger<AeroDBMessageStore>.Instance,
                loggerFactory: null,
                storeOptions: options,
                ownsClient: false,
                storeIdentity: location.PhysicalIdentity);
            store.DemoteToAncillary();
            await store.MigrateAsync();

            var runtime = Substitute.For<IWolverineRuntime>();
            var runtimeOptions = CreateOptionsWithPositiveAssignedNode();
            runtimeOptions.Durability.FirstNodeReassignmentExecution = TimeSpan.FromHours(1);
            runtime.Options.Returns(runtimeOptions);
            runtime.Logger.Returns(NullLogger.Instance);
            runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

            var endpoints = Substitute.For<IEndpointCollection>();
            endpoints.FindListenerCircuit(Arg.Any<Uri>()).Returns((IListenerCircuit?)null);
            runtime.Endpoints.Returns(endpoints);

            var main = Substitute.For<IMessageStore>();
            main.Role.Returns(MessageStoreRole.Main);
            main.Nodes.Returns(authority);
            main.TenantIds.Returns(new List<string>());
            main.Uri.Returns(new Uri("aerodb://authority/main"));
            var stores = new MessageStoreCollection(runtime, [main], Array.Empty<AncillaryMessageStore>());
            runtime.Stores.Returns(stores);
            runtime.Storage.Returns(stores.Main);

            var agent = new AeroDBDurabilityAgent(location.PhysicalIdentity, store, runtime);
            await agent.StartAsync(CancellationToken.None);
            return new TenantRecoveryFixture(client, documents, store, agent, endpoints, runtimeOptions.Durability.AssignedNodeNumber);
        }

        private static WolverineOptions CreateOptionsWithPositiveAssignedNode()
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var options = new WolverineOptions();
                if (options.Durability.AssignedNodeNumber > 0 && options.Durability.AssignedNodeNumber != 41
                    && options.Durability.AssignedNodeNumber != 77)
                    return options;
            }

            throw new InvalidOperationException("Unable to create a Wolverine test options instance with a positive assigned node number.");
        }

        public async Task<Guid> StoreIncomingAsync(int ownerId)
        {
            var envelope = new Envelope
            {
                Id = Guid.NewGuid(),
                Data = "tenant-recovery"u8.ToArray(),
                MessageType = "tests.tenant-recovery",
                Status = EnvelopeStatus.Incoming,
                Destination = new Uri("loopback://tenant-recovery")
            };
            await Store.StoreIncomingAsync(envelope);
            var update = await _client.RawQuery(
                $"UPDATE wolverine_incoming_envelopes:`{envelope.Id}` SET owner_id = {ownerId};");
            update.EnsureAllOks();
            return envelope.Id;
        }

        public async Task<int> OwnerIdAsync(Guid id)
        {
            var response = await _client.RawQuery(
                $"SELECT owner_id FROM wolverine_incoming_envelopes:`{id}`;");
            response.EnsureAllOks();
            var rows = response.GetValue<List<OwnerRow>>(0) ?? [];
            rows.Count.ShouldBe(1);
            return rows[0].owner_id;
        }

        public async Task StoreOutgoingAsync(int ownerId)
        {
            var envelope = new Envelope
            {
                Id = Guid.NewGuid(),
                Data = "tenant-recovery"u8.ToArray(),
                MessageType = "tests.tenant-recovery",
                Destination = new Uri("loopback://tenant-recovery-outgoing")
            };
            await Store.StoreOutgoingAsync(envelope, ownerId);
        }

        public async ValueTask DisposeAsync()
        {
            await Agent.StopAsync(CancellationToken.None);
            await Store.DisposeAsync();
            await _documents.DisposeAsync();
            await _client.DisposeAsync();
        }
    }

    private static AeroDBMessageStore RawStore(AeroDBTenantStoreLocation location) =>
        CreateRawStore(location);

    private static AeroDBMessageStore CreateRawStore(AeroDBTenantStoreLocation location)
    {
        var raw = new AeroDBMessageStore(Substitute.For<ISurrealDbClient>(), NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null, storeOptions: null, ownsClient: false,
            storeIdentity: location.PhysicalIdentity);
        raw.TenantIds.Add(location.TenantId);
        return raw;
    }
}
