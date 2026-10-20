using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Tcp;

namespace AeroDB.Tests;

/// <summary>
/// Documents the required behavior when a balanced node can no longer write its
/// coordinator heartbeat. The failure is injected by SurrealDB itself, not by
/// replacing Wolverine's coordinator, scheduler, or leader-transition code.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class WolverineLeadershipFailureServerTests
{
    private static readonly Uri SingularAgentUri = new("heartbeat-failure-singular://");

    [Test]
    public async Task Balanced_leader_steps_down_and_stops_its_singular_agent_when_its_coordinator_heartbeat_fails(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var leaderDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var peerDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        using var leaderHost = await StartBalancedHostAsync(leaderDocumentStore, cancellationToken);
        var leaderRuntime = leaderHost.Services
            .GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        await WaitUntilAsync(
            () => leaderRuntime.IsLeader() && leaderRuntime.Agents.AllRunningAgentUris().Contains(SingularAgentUri),
            TimeSpan.FromSeconds(15),
            cancellationToken);

        var originalAgentIsRunningBeforeFault = leaderRuntime.Agents.AllRunningAgentUris().Contains(SingularAgentUri);
        originalAgentIsRunningBeforeFault.ShouldBeTrue(
            "The elected original node must own the singular agent before its coordinator heartbeat is faulted.");

        var eventDefinition = await leaderDocumentStore.Client.RawQuery(
            "DEFINE EVENT reject_leader_heartbeat ON TABLE wolverine_nodes " +
            $"WHEN ($event = 'CREATE' OR $event = 'UPDATE') AND $after.id = wolverine_nodes:`{leaderRuntime.Options.UniqueNodeId}` THEN {{ " +
            "THROW 'synthetic coordinator heartbeat failure'; };",
            cancellationToken: cancellationToken);
        eventDefinition.EnsureAllOks();

        await Should.ThrowAsync<ResponseUnsuccessfulException>(
            () => leaderRuntime.Agents.DoHealthChecksAsync());

        // Start the peer only after the original node's real coordinator write
        // has failed, so SingularAgent's normal non-leader preference cannot
        // relocate the original assignment before the partition is established.
        using var peerHost = await StartBalancedHostAsync(peerDocumentStore, cancellationToken);
        var peerRuntime = peerHost.Services
            .GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        await WaitUntilAsync(
            () => peerRuntime.IsLeader() && peerRuntime.Agents.AllRunningAgentUris().Contains(SingularAgentUri),
            TimeSpan.FromSeconds(15),
            cancellationToken);

        // Allow at most one bounded post-takeover observation window (one lease
        // timeout plus several public heartbeat polls) for independent stale
        // node processing; this path has no leader handoff notification.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        // The desired contract is that the failed node relinquishes leadership
        // and stops locally-assigned singular work before its healthy peer owns it.
        var formerPersistenceLease = leaderHost.Services
            .GetRequiredService<AeroDBMessageStore>()
            .HasLeadershipLock();
        var formerLeader = leaderRuntime.IsLeader();
        var formerAgentIsRunning = leaderRuntime.Agents.AllRunningAgentUris().Contains(SingularAgentUri);
        var peerIsLeader = peerRuntime.IsLeader();
        var peerAgentIsRunning = peerRuntime.Agents.AllRunningAgentUris().Contains(SingularAgentUri);
        var observedState =
            $"formerPersistenceLease={formerPersistenceLease}, formerLeader={formerLeader}, " +
            $"formerAgentIsRunning={formerAgentIsRunning}, " +
            $"peerLeader={peerIsLeader}, peerAgentIsRunning={peerAgentIsRunning}";

        formerPersistenceLease.ShouldBeFalse(observedState);
        formerLeader.ShouldBeFalse(observedState);
        formerAgentIsRunning.ShouldBeFalse(observedState);
        peerIsLeader.ShouldBeTrue(observedState);
        peerAgentIsRunning.ShouldBeTrue(observedState);
    }

    private static async Task<IHost> StartBalancedHostAsync(
        IDocumentStore documentStore,
        CancellationToken cancellationToken)
    {
        var client = documentStore.Client;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(documentStore);
                services.AddSingleton<ISurrealDbClient>(client);
            })
            .UseWolverine(options =>
            {
                options.RestoreV5Defaults();
                options.ApplicationAssembly = typeof(WolverineLeadershipFailureServerTests).Assembly;
                options.Durability.Mode = DurabilityMode.Balanced;
                options.Durability.FirstHealthCheckExecution = TimeSpan.FromMilliseconds(100);
                options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(200);
                options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(1);
                options.Durability.CheckAssignmentPeriod = TimeSpan.FromMilliseconds(100);
                options.Transports.NodeControlEndpoint = new TcpEndpoint(
                    "127.0.0.1",
                    AuthenticatedWolverineFixture.GetAvailableLoopbackPort());

                options.Services.AddSingleton<AeroDBMessageStore>(serviceProvider =>
                    new AeroDBMessageStore(
                        client,
                        serviceProvider.GetRequiredService<ILogger<AeroDBMessageStore>>(),
                        serviceProvider.GetService<ILoggerFactory>(),
                        documentStore.Options,
                        ownsClient: false));
                options.Services.AddSingleton<IMessageStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<AeroDBMessageStore>());
                options.Services.AddSingleton(serviceProvider => new AeroDBOutboxedSessionFactory(
                    documentStore,
                    serviceProvider.GetRequiredService<AeroDBMessageStore>(),
                    serviceProvider.GetRequiredService<ILogger<AeroDBOutboxedSessionFactory>>()));
                options.Services.AddScoped<ScopedDocumentSessionHolder>();
                options.Services.AddSingleton<IWolverineExtension>(new AeroDBIntegration());
                options.Services.AddSingularAgent<HeartbeatFailureSingularAgent>();
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return host;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (!condition())
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The expected Wolverine state was not reached within {timeout}.");
        }
    }

    private sealed class HeartbeatFailureSingularAgent : SingularAgent
    {
        public HeartbeatFailureSingularAgent() : base("heartbeat-failure-singular")
        {
        }

        protected override Task startAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task stopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
