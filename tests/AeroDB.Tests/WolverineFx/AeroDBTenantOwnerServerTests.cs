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
/// Proves that fixed tenant stores use the main runtime's real database-issued
/// owner, including tenant stores introduced after that runtime has persisted.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBTenantOwnerServerTests
{
    [Test]
    public async Task Shared_main_identity_routes_late_tenant_inbox_and_outbox_to_the_actual_database_assigned_owner(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var priorDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var mainDocumentStore = await fixture.CreateInitializedStoreAsync(cancellationToken);
        await using var priorStore = await CreateMessageStoreAsync(priorDocumentStore, cancellationToken);
        var priorOptions = RuntimeOptions();
        var priorNode = RuntimeWolverineNode.For(priorOptions);
        priorStore.Initialize(RuntimeFor(priorOptions));
        var priorOwner = await priorStore.PersistAsync(priorNode, cancellationToken);

        var sharedIdentity = new AeroDBRuntimeNodeIdentity();
        await using var mainStore = new AeroDBMessageStore(
            mainDocumentStore.Client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: mainDocumentStore.Options,
            ownsClient: false,
            runtimeIdentity: sharedIdentity);
        await mainStore.InitializeSchemaAsync();
        var mainOptions = RuntimeOptions();
        var mainRuntime = RuntimeFor(mainOptions);
        var mainNode = RuntimeWolverineNode.For(mainOptions);
        mainStore.Initialize(mainRuntime);
        var mainOwner = await mainStore.PersistAsync(mainNode, cancellationToken);

        mainOwner.ShouldBeGreaterThan(1);
        mainOwner.ShouldNotBe(priorOwner);
        var mainIncoming = EnvelopeFor("main-runtime-incoming");
        await mainStore.StoreIncomingAsync(mainIncoming);
        (await OwnerIdAsync(mainDocumentStore, "wolverine_incoming_envelopes", mainIncoming.Id, cancellationToken))
            .ShouldBe(mainOwner);

        var alpha = Location("alpha");
        var beta = Location("beta");
        await DefineScopeAsync(mainDocumentStore, alpha, cancellationToken);
        await DefineScopeAsync(mainDocumentStore, beta, cancellationToken);

        var registry = new AeroDBTenantStoreRegistry();
        var alphaStage = registry.Stage([alpha]);
        registry.Activate(alphaStage);
        var tenantDocuments = new FixtureTenantDocuments(fixture);
        var pairs = new AeroDBTenantStorePairCache(
            registry,
            new AeroDBTenantStorePairFactory(
                tenantDocuments,
                NullLogger<AeroDBMessageStore>.Instance,
                runtimeIdentity: sharedIdentity));
        await using (pairs)
        {
            var alphaPair = await pairs.GetAsync(alpha.TenantId, cancellationToken);
            await alphaPair.MessageStore.InitializeSchemaAsync();
            alphaPair.MessageStore.GetOwnerId().ShouldBe(mainOwner);

            var betaStage = registry.Stage([alpha, beta]);
            registry.Activate(betaStage);
            var betaPair = await pairs.GetAsync(beta.TenantId, cancellationToken);
            await betaPair.MessageStore.InitializeSchemaAsync();
            betaPair.MessageStore.GetOwnerId().ShouldBe(mainOwner);

            var alphaIncoming = EnvelopeFor("tenant-alpha-incoming");
            var alphaOutgoing = EnvelopeFor("tenant-alpha-outgoing");
            var betaIncoming = EnvelopeFor("tenant-beta-incoming");
            var betaOutgoing = EnvelopeFor("tenant-beta-outgoing");
            await alphaPair.DurableMessageStore.Inbox.StoreIncomingAsync(alphaIncoming);
            await alphaPair.DurableMessageStore.Outbox.StoreOutgoingAsync(alphaOutgoing, mainOwner);
            await betaPair.DurableMessageStore.Inbox.StoreIncomingAsync(betaIncoming);
            await betaPair.DurableMessageStore.Outbox.StoreOutgoingAsync(betaOutgoing, mainOwner);

            (await OwnerIdAsync(alphaPair.DocumentStore, "wolverine_incoming_envelopes", alphaIncoming.Id, cancellationToken))
                .ShouldBe(mainOwner);
            (await OwnerIdAsync(alphaPair.DocumentStore, "wolverine_outgoing_envelopes", alphaOutgoing.Id, cancellationToken))
                .ShouldBe(mainOwner);
            (await OwnerIdAsync(betaPair.DocumentStore, "wolverine_incoming_envelopes", betaIncoming.Id, cancellationToken))
                .ShouldBe(mainOwner);
            (await OwnerIdAsync(betaPair.DocumentStore, "wolverine_outgoing_envelopes", betaOutgoing.Id, cancellationToken))
                .ShouldBe(mainOwner);
            (await RecordCountAsync(alphaPair.DocumentStore, "wolverine_incoming_envelopes", betaIncoming.Id, cancellationToken))
                .ShouldBe(0);
            (await RecordCountAsync(betaPair.DocumentStore, "wolverine_outgoing_envelopes", alphaOutgoing.Id, cancellationToken))
                .ShouldBe(0);

            var conflictingOptions = RuntimeOptions();
            Should.Throw<InvalidOperationException>(
                () => betaPair.DurableMessageStore.Initialize(RuntimeFor(conflictingOptions)));
        }
    }

    private static AeroDBTenantStoreLocation Location(string tenantId) =>
        new(tenantId, "coordinator", "ca_" + tenantId, "durability");

    private static async Task DefineScopeAsync(
        IDocumentStore coordinator,
        AeroDBTenantStoreLocation location,
        CancellationToken cancellationToken)
    {
        await using var administrator = Documents.For(options =>
        {
            options.Endpoint = coordinator.Options.Endpoint;
            options.Namespace = "bootstrap";
            options.Database = "bootstrap";
            options.Username = coordinator.Options.Username;
            options.Password = coordinator.Options.Password;
        });
        await administrator.InitializeAsync(cancellationToken);
        await using var session = await administrator.LightweightSessionAsync(cancellationToken);
        await session.ExecuteSqlAsync(
            $"DEFINE NAMESPACE IF NOT EXISTS `{location.Namespace}`;",
            null,
            cancellationToken);
        await session.ExecuteSqlAsync($"USE NS `{location.Namespace}`;", null, cancellationToken);
        await session.ExecuteSqlAsync(
            $"DEFINE DATABASE IF NOT EXISTS `{location.Database}`;",
            null,
            cancellationToken);
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
            ApplicationAssembly = typeof(AeroDBTenantOwnerServerTests).Assembly
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

    private static Envelope EnvelopeFor(string messageType) => new()
    {
        Id = Guid.NewGuid(),
        Destination = new Uri("loopback://tenant-owner"),
        MessageType = messageType,
        Data = "tenant-owner"u8.ToArray()
    };

    private static async Task<int> OwnerIdAsync(
        IDocumentStore store,
        string table,
        Guid envelopeId,
        CancellationToken cancellationToken)
    {
        var response = await store.Client.RawQuery(
            $"SELECT VALUE owner_id FROM {table}:`{envelopeId:D}`;",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        return (response.GetValue<List<int>>(0)
            ?? throw new InvalidOperationException("The durable envelope ownership query returned no result set."))
            .ShouldHaveSingleItem();
    }

    private static async Task<int> RecordCountAsync(
        IDocumentStore store,
        string table,
        Guid envelopeId,
        CancellationToken cancellationToken)
    {
        var response = await store.Client.RawQuery(
            $"SELECT * FROM {table}:`{envelopeId:D}`;",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        var rows = response.GetValue<List<Dictionary<string, object>>>(0)
            ?? throw new InvalidOperationException("The durable envelope isolation query returned no result set.");
        return rows.Count;
    }

    private sealed class FixtureTenantDocuments(AuthenticatedWolverineFixture fixture) : IAeroDBTenantDocumentStoreFactory
    {
        public async ValueTask<IDocumentStore> CreateAsync(
            AeroDBTenantStoreLocation location,
            CancellationToken cancellationToken = default) =>
            await fixture.CreateInitializedStoreAsync(
                cancellationToken,
                options =>
                {
                    options.Namespace = location.Namespace;
                    options.Database = location.Database;
                });
    }
}
