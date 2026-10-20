using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;

namespace AeroDB.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class FencedOutboxServerTests
{
    [Test]
    public async Task Balanced_runtime_owner_is_assigned_once_and_used_by_a_late_tenant_factory_context_outbox(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        const string mainNamespace = "runtime_owner_main";
        const string mainDatabase = "durability";
        var alpha = new AeroDBTenantStoreLocation("alpha", "runtime-owner", "ca_runtime_owner_alpha", "durability");

        await using var priorDocumentStore = await fixture.CreateInitializedStoreAsync(
            mainNamespace,
            mainDatabase,
            cancellationToken,
            ConfigureDocumentMappings);
        await using var mainDocumentStore = await fixture.CreateInitializedStoreAsync(
            mainNamespace,
            mainDatabase,
            cancellationToken,
            ConfigureDocumentMappings);
        using var priorHost = await StartBalancedHostAsync(priorDocumentStore, runtimeIdentity: null, cancellationToken);
        var priorStore = priorHost.Services.GetRequiredService<AeroDBMessageStore>();
        await WaitUntilAsync(() => HasAssignedOwner(priorStore), cancellationToken);
        var priorOwner = priorStore.GetOwnerId();

        var sharedIdentity = new AeroDBRuntimeNodeIdentity();
        using var mainHost = await StartBalancedHostAsync(mainDocumentStore, sharedIdentity, cancellationToken);
        var mainRuntime = mainHost.Services
            .GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();
        var mainStore = mainHost.Services.GetRequiredService<AeroDBMessageStore>();
        await WaitUntilAsync(() => HasAssignedOwner(mainStore), cancellationToken);
        var mainOwner = mainStore.GetOwnerId();

        mainOwner.ShouldBeGreaterThan(1);
        mainOwner.ShouldNotBe(priorOwner);

        await using (var alphaSeedStore = await fixture.CreateInitializedStoreAsync(
            alpha.Namespace,
            alpha.Database,
            cancellationToken,
            ConfigureDocumentMappings))
        {
            await using var alphaSchemaStore = await CreateMessageStoreAsync(alphaSeedStore);
            await SeedWorkspaceAsync(
                alphaSeedStore,
                new WorkspaceFence { Id = 601, Version = 7 },
                cancellationToken);
        }

        var registry = new AeroDBTenantStoreRegistry();
        registry.Activate(registry.Stage([alpha]));
        await using var pairs = new AeroDBTenantStorePairCache(
            registry,
            new AeroDBTenantStorePairFactory(
                new FixtureTenantDocuments(fixture),
                NullLogger<AeroDBMessageStore>.Instance,
                runtimeIdentity: sharedIdentity));
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(pairs);
        var context = new MessageContext(mainRuntime) { TenantId = alpha.TenantId };
        var envelope = CreateEnvelope();
        var job = new PrivateJob { Id = 602, Name = "late-tenant-factory-outbox" };

        await using (var session = await factory.OpenSessionAsync(context, cancellationToken))
        {
            var fence = session.FenceExpectedVersion<WorkspaceFence>(601, expectedVersion: 7);
            session.Insert(job);
            var transaction = context.Transaction
                ?? throw new InvalidOperationException("The factory did not enlist the message context in an outbox transaction.");
            await transaction.PersistOutgoingAsync(envelope);
            await session.SaveChangesAsync(cancellationToken);

            fence.Status.ShouldBe(VersionFenceStatus.Committed);
            fence.CommittedVersion.ShouldBe(8);
        }

        var alphaPair = await pairs.GetAsync(alpha.TenantId, cancellationToken);
        context.Storage.ShouldBeSameAs(alphaPair.MessageStore);
        alphaPair.MessageStore.GetOwnerId().ShouldBe(mainOwner);
        envelope.WasPersistedInOutbox.ShouldBeTrue();
        (await OwnerIdAsync(alphaPair.DocumentStore, envelope.Id, cancellationToken)).ShouldBe(mainOwner);
        (await LoadJobAsync(alphaPair.DocumentStore, job.Id, cancellationToken)).ShouldNotBeNull();
        (await RecordCountAsync(mainDocumentStore, "wolverine_outgoing_envelopes", envelope.Id, cancellationToken))
            .ShouldBe(0);
        (await LoadJobAsync(mainDocumentStore, job.Id, cancellationToken)).ShouldBeNull();
    }

    [Test]
    public async Task Fenced_save_commits_workspace_fence_private_job_and_outgoing_envelope_together(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var store = await fixture.CreateInitializedStoreAsync(
            cancellationToken,
            ConfigureDocumentMappings);
        var messageStore = await CreateMessageStoreAsync(store);
        var workspace = new WorkspaceFence { Id = 101, Version = 1 };
        var job = new PrivateJob { Id = 102, Name = "fenced-export" };
        var envelope = CreateEnvelope();

        await SeedWorkspaceAsync(store, workspace, cancellationToken);

        await using var session = await store.LightweightSessionAsync(cancellationToken);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, ownerId: 0);
        var fence = session.FenceExpectedVersion<WorkspaceFence>(workspace.Id, workspace.Version);
        session.Insert(job);
        await transaction.PersistOutgoingAsync(envelope);

        await session.SaveChangesAsync(cancellationToken);
        transaction.MarkCommitted();

        fence.Status.ShouldBe(VersionFenceStatus.Committed);
        fence.CommittedVersion.ShouldBe(2);
        envelope.WasPersistedInOutbox.ShouldBeTrue();
        (await LoadWorkspaceAsync(store, workspace.Id, cancellationToken)).Version.ShouldBe(2);
        (await LoadJobAsync(store, job.Id, cancellationToken)).Name.ShouldBe(job.Name);

        var persisted = await messageStore.AllOutgoingAsync();
        persisted.ShouldHaveSingleItem().Id.ShouldBe(envelope.Id);
        persisted.Single().Data.ShouldBe(envelope.Data);
        persisted.Single().ScheduledTime.ShouldBe(envelope.ScheduledTime);
        persisted.Single().DeliverBy.ShouldBe(envelope.DeliverBy);
        persisted.Single().KeepUntil.ShouldBe(envelope.KeepUntil);

        var recovered = await messageStore.LoadOutgoingAsync(envelope.Destination!);
        recovered.ShouldHaveSingleItem().Id.ShouldBe(envelope.Id);
        recovered.Single().Status.ShouldBe(EnvelopeStatus.Outgoing);
        recovered.Single().Data.ShouldBe(envelope.Data);
        recovered.Single().ScheduledTime.ShouldBe(envelope.ScheduledTime);
        recovered.Single().DeliverBy.ShouldBe(envelope.DeliverBy);
        recovered.Single().KeepUntil.ShouldBe(envelope.KeepUntil);
    }

    [Test]
    public async Task Stale_workspace_fence_rolls_back_private_job_and_outgoing_envelope(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var store = await fixture.CreateInitializedStoreAsync(
            cancellationToken,
            ConfigureDocumentMappings);
        var messageStore = await CreateMessageStoreAsync(store);
        var workspace = new WorkspaceFence { Id = 201, Version = 2 };
        var job = new PrivateJob { Id = 202, Name = "must-not-persist" };
        var envelope = CreateEnvelope();

        await SeedWorkspaceAsync(store, workspace, cancellationToken);

        await using var session = await store.LightweightSessionAsync(cancellationToken);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, ownerId: 0);
        var fence = session.FenceExpectedVersion<WorkspaceFence>(workspace.Id, expectedVersion: 1);
        session.Insert(job);
        await transaction.PersistOutgoingAsync(envelope);

        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync(cancellationToken));

        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        fence.CommittedVersion.ShouldBeNull();
        envelope.WasPersistedInOutbox.ShouldBeFalse();
        (await LoadWorkspaceAsync(store, workspace.Id, cancellationToken)).Version.ShouldBe(2);
        (await LoadJobAsync(store, job.Id, cancellationToken)).ShouldBeNull();
        (await messageStore.AllOutgoingAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Later_precommit_failure_rolls_back_fence_private_job_and_outgoing_envelope(
        CancellationToken cancellationToken)
    {
        var failBeforeCommit = new ThrowBeforeCommitListener();
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var store = await fixture.CreateInitializedStoreAsync(
            cancellationToken,
            options =>
            {
                ConfigureDocumentMappings(options);
                options.Listeners.Add(failBeforeCommit);
            });
        var messageStore = await CreateMessageStoreAsync(store);
        var workspace = new WorkspaceFence { Id = 301, Version = 5 };
        var job = new PrivateJob { Id = 302, Name = "must-rollback" };
        var envelope = CreateEnvelope();

        await SeedWorkspaceAsync(store, workspace, cancellationToken);
        failBeforeCommit.Arm();

        await using var session = await store.LightweightSessionAsync(cancellationToken);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, ownerId: 0);
        var fence = session.FenceExpectedVersion<WorkspaceFence>(workspace.Id, workspace.Version);
        session.Insert(job);
        await transaction.PersistOutgoingAsync(envelope);

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(cancellationToken));

        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        fence.CommittedVersion.ShouldBeNull();
        envelope.WasPersistedInOutbox.ShouldBeFalse();
        (await LoadWorkspaceAsync(store, workspace.Id, cancellationToken)).Version.ShouldBe(5);
        (await LoadJobAsync(store, job.Id, cancellationToken)).ShouldBeNull();
        (await messageStore.AllOutgoingAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Missing_incoming_acknowledgement_rolls_back_private_job(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var store = await fixture.CreateInitializedStoreAsync(
            cancellationToken,
            ConfigureDocumentMappings);
        var messageStore = await CreateMessageStoreAsync(store);
        var missingIncoming = CreateEnvelope();
        var job = new PrivateJob { Id = 402, Name = "must-rollback-with-missing-ack" };

        await using var session = await store.LightweightSessionAsync(cancellationToken);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, ownerId: 0);
        session.Insert(job);
        transaction.QueueHandlingForPreviouslyPersistedIncoming(missingIncoming);

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(cancellationToken));

        missingIncoming.WasPersistedInInbox.ShouldBeFalse();
        missingIncoming.Status.ShouldNotBe(EnvelopeStatus.Handled);
        (await LoadJobAsync(store, job.Id, cancellationToken)).ShouldBeNull();
        (await messageStore.AllIncomingAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Duplicate_outgoing_identifier_rolls_back_private_job_without_acknowledging_the_duplicate(
        CancellationToken cancellationToken)
    {
        await using var fixture = await AuthenticatedWolverineFixture.StartAsync(cancellationToken);
        await using var store = await fixture.CreateInitializedStoreAsync(
            cancellationToken,
            ConfigureDocumentMappings);
        var messageStore = await CreateMessageStoreAsync(store);
        var original = CreateEnvelope();
        var duplicate = CreateEnvelope();
        duplicate.Id = original.Id;
        duplicate.Data = "duplicate-must-not-replace-original"u8.ToArray();
        var job = new PrivateJob { Id = 502, Name = "must-rollback-with-duplicate" };
        await messageStore.StoreOutgoingAsync(original, ownerId: 0);

        await using var session = await store.LightweightSessionAsync(cancellationToken);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, ownerId: 0);
        session.Insert(job);
        await transaction.PersistOutgoingAsync(duplicate);

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(cancellationToken));

        duplicate.WasPersistedInOutbox.ShouldBeFalse();
        (await LoadJobAsync(store, job.Id, cancellationToken)).ShouldBeNull();
        var persisted = await messageStore.AllOutgoingAsync();
        persisted.ShouldHaveSingleItem().Id.ShouldBe(original.Id);
        persisted.Single().Data.ShouldBe(original.Data);
    }

    private static void ConfigureDocumentMappings(StoreOptions options)
    {
        options.Schema.For<WorkspaceFence>().SetSchemaMode(SchemaMode.Flexible);
        options.Schema.For<PrivateJob>().SetSchemaMode(SchemaMode.Flexible);
    }

    private static async Task<AeroDBMessageStore> CreateMessageStoreAsync(IDocumentStore store)
    {
        var messageStore = new AeroDBMessageStore(
            store.Client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: store.Options,
            ownsClient: false);
        await messageStore.InitializeSchemaAsync();
        return messageStore;
    }

    private static async Task<IHost> StartBalancedHostAsync(
        IDocumentStore documentStore,
        AeroDBRuntimeNodeIdentity? runtimeIdentity,
        CancellationToken cancellationToken)
    {
        var client = documentStore.Client;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddSingleton(documentStore);
                services.AddSingleton<ISurrealDbClient>(client);
            })
            .UseWolverine(options =>
            {
                options.RestoreV5Defaults();
                options.ApplicationAssembly = typeof(FencedOutboxServerTests).Assembly;
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
                        ownsClient: false,
                        runtimeIdentity: runtimeIdentity));
                options.Services.AddSingleton<IMessageStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<AeroDBMessageStore>());
                options.Services.AddSingleton(serviceProvider => new AeroDBOutboxedSessionFactory(
                    documentStore,
                    serviceProvider.GetRequiredService<AeroDBMessageStore>(),
                    serviceProvider.GetRequiredService<ILogger<AeroDBOutboxedSessionFactory>>()));
                options.Services.AddScoped<ScopedDocumentSessionHolder>();
                options.Services.AddSingleton<IWolverineExtension>(new AeroDBIntegration());
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return host;
    }

    private static bool HasAssignedOwner(AeroDBMessageStore store)
    {
        try
        {
            return store.GetOwnerId() > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!condition())
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Balanced Wolverine host did not receive a database-assigned owner number.");
        }
    }

    private static async Task<int> OwnerIdAsync(
        IDocumentStore store,
        Guid envelopeId,
        CancellationToken cancellationToken)
    {
        var response = await store.Client.RawQuery(
            $"SELECT VALUE owner_id FROM wolverine_outgoing_envelopes:`{envelopeId:D}`;",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        return (response.GetValue<List<int>>(0)
            ?? throw new InvalidOperationException("The durable envelope ownership query returned no result set."))
            .ShouldHaveSingleItem();
    }

    private static async Task<int> RecordCountAsync(
        IDocumentStore store,
        string table,
        Guid id,
        CancellationToken cancellationToken)
    {
        var response = await store.Client.RawQuery(
            $"SELECT * FROM {table}:`{id:D}`;",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        return (response.GetValue<List<Dictionary<string, object>>>(0)
            ?? throw new InvalidOperationException("The tenant-isolation query returned no result set."))
            .Count;
    }

    private static async Task SeedWorkspaceAsync(
        IDocumentStore store,
        WorkspaceFence workspace,
        CancellationToken cancellationToken)
    {
        await using var session = await store.LightweightSessionAsync(cancellationToken);
        session.Insert(workspace);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<WorkspaceFence> LoadWorkspaceAsync(
        IDocumentStore store,
        long id,
        CancellationToken cancellationToken)
    {
        await using var session = await store.LightweightSessionAsync(cancellationToken);
        return (await session.LoadAsync<WorkspaceFence>(id, cancellationToken))
            ?? throw new InvalidOperationException($"Workspace fence '{id}' was not found.");
    }

    private static async Task<PrivateJob?> LoadJobAsync(
        IDocumentStore store,
        long id,
        CancellationToken cancellationToken)
    {
        await using var session = await store.LightweightSessionAsync(cancellationToken);
        return await session.LoadAsync<PrivateJob>(id, cancellationToken);
    }

    private static Envelope CreateEnvelope()
    {
        var scheduled = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "contractor.private-job",
            Destination = new Uri("loopback://fenced-outbox"),
            ScheduledTime = scheduled,
            DeliverBy = scheduled.AddHours(2),
            KeepUntil = scheduled.AddDays(1),
            Data = "fenced-payload"u8.ToArray()
        };
    }

    private sealed class ThrowBeforeCommitListener : IDocumentSessionListener
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct) =>
            _armed
                ? Task.FromException(new InvalidOperationException("Synthetic pre-commit failure."))
                : Task.CompletedTask;
    }

    private sealed class FixtureTenantDocuments(AuthenticatedWolverineFixture fixture) : IAeroDBTenantDocumentStoreFactory
    {
        public async ValueTask<IDocumentStore> CreateAsync(
            AeroDBTenantStoreLocation location,
            CancellationToken cancellationToken = default) =>
            await fixture.CreateUninitializedStoreAsync(
                location.Namespace,
                location.Database,
                cancellationToken,
                ConfigureDocumentMappings);
    }

    public sealed class WorkspaceFence : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
    }

    public sealed class PrivateJob : SableDocument<long>
    {
        public string Name { get; set; } = string.Empty;
    }
}
