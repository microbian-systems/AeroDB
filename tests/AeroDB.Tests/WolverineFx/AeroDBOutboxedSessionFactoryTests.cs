using AeroDB.Sable;
using AeroDB.WolverineFx;

namespace AeroDB.Tests;

using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using JasperFx.MultiTenancy;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

/// <summary>
/// Mock-based unit tests for <see cref="AeroDBOutboxedSessionFactory"/>.
/// AeroDBMessageStore is sealed — tests create real instances with a mocked
/// ISurrealDbClient. Verifies session opening, tenant scoping, and outbox enlistment.
/// </summary>
public class AeroDBOutboxedSessionFactoryTests
{
    [Test]
    public async Task Public_constructor_owns_the_client_and_disposes_it_once_across_repeated_and_concurrent_calls()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var disposeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisposeAsync().Returns(new ValueTask(disposeCompletion.Task));
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

        var disposals = Enumerable.Range(0, 16)
            .Select(_ => store.DisposeAsync().AsTask())
            .ToArray();

        disposals.All(task => !task.IsCompleted).ShouldBeTrue();
        disposals.Skip(1).All(task => ReferenceEquals(disposals[0], task)).ShouldBeTrue();

        disposeCompletion.SetResult();
        await Task.WhenAll(disposals);
        await store.DisposeAsync();

        await client.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Faulted_owned_client_disposal_is_shared_and_never_retried()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var expected = new InvalidOperationException("Simulated client disposal failure.");
        client.DisposeAsync().Returns(new ValueTask(Task.FromException(expected)));
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

        var disposals = Enumerable.Range(0, 16)
            .Select(_ => store.DisposeAsync().AsTask())
            .ToArray();

        disposals.Skip(1).All(task => ReferenceEquals(disposals[0], task)).ShouldBeTrue();

        var failures = await Task.WhenAll(disposals.Select(async task =>
            await Should.ThrowAsync<InvalidOperationException>(() => task)));
        foreach (var failure in failures)
            ReferenceEquals(expected, failure).ShouldBeTrue();

        var retry = store.DisposeAsync().AsTask();
        ReferenceEquals(disposals[0], retry).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => retry);
        await client.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Borrowed_client_mode_never_disposes_the_client()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = new AeroDBMessageStore(
            client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: null,
            ownsClient: false);

        await store.DisposeAsync();
        await store.DisposeAsync();

        await client.DidNotReceive().DisposeAsync();
    }

    [Test]
    public async Task Internal_owning_client_mode_releases_the_client_once()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = new AeroDBMessageStore(
            client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: null,
            ownsClient: true);

        await store.DisposeAsync();
        await store.DisposeAsync();

        await client.Received(1).DisposeAsync();
    }

    [Test]
    public void Tenant_store_location_identity_uses_a_documented_big_endian_utf8_encoding()
    {
        var location = new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main");

        location.PhysicalKey.ShouldBe("71e820872e345e08b91e975d624cd8d40e9878f0c2823153076696f5c3b65e64");
    }

    [Test]
    public void Tenant_store_location_rejects_a_cluster_alias_that_is_not_a_bounded_ascii_identifier()
    {
        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStoreLocation("tenant-alpha", "primary/credentials", "ca_alpha", "main"));
    }

    [Test]
    public void Tenant_store_location_rejects_invalid_utf16_before_hashing()
    {
        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_\uD800alpha", "main"));
    }

    [Test]
    public void Tenant_store_location_rejects_values_larger_than_the_server_owned_identifier_bound()
    {
        var oversized = new string('x', 256);

        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStoreLocation(oversized, "primary", "ca_alpha", "main"));
        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", oversized, "main"));
        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", oversized));
    }

    [Test]
    public void Tenant_store_location_identity_is_stable_and_does_not_reveal_storage_names()
    {
        var first = new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main");
        var samePhysicalLocationWithAnotherTenant = new AeroDBTenantStoreLocation(
            "tenant-beta", "primary", "ca_alpha", "main");
        var differentPhysicalLocation = new AeroDBTenantStoreLocation(
            "tenant-alpha", "primary", "ca_alpha", "other");

        first.PhysicalKey.ShouldBe(samePhysicalLocationWithAnotherTenant.PhysicalKey);
        first.PhysicalKey.ShouldNotBe(differentPhysicalLocation.PhysicalKey);
        first.PhysicalIdentity.Scheme.ShouldBe("wolverinedb");
        first.PhysicalIdentity.AbsoluteUri.ShouldNotContain("ca_alpha");
        first.PhysicalIdentity.AbsoluteUri.ShouldNotContain("primary");
    }

    [Test]
    public void Tenant_store_snapshot_rejects_duplicate_physical_locations()
    {
        var locations = new[]
        {
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main"),
            new AeroDBTenantStoreLocation("tenant-beta", "primary", "ca_alpha", "main")
        };

        Should.Throw<InvalidOperationException>(() =>
            AeroDBTenantStoreDirectorySnapshot.Create(locations));
    }

    [Test]
    public void Tenant_store_snapshot_rejects_duplicate_tenant_ids_before_publication()
    {
        var locations = new[]
        {
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main"),
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha_other", "main")
        };

        Should.Throw<InvalidOperationException>(() =>
            AeroDBTenantStoreDirectorySnapshot.Create(locations));
    }

    [Test]
    public void Tenant_store_snapshot_keeps_ordinally_distinct_tenant_ids()
    {
        var snapshot = AeroDBTenantStoreDirectorySnapshot.Create(new[]
        {
            new AeroDBTenantStoreLocation("Tenant-Alpha", "primary", "ca_alpha_upper", "main"),
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha_lower", "main")
        });

        snapshot.ByTenant.Count.ShouldBe(2);
        snapshot.ByTenant.ContainsKey("Tenant-Alpha").ShouldBeTrue();
        snapshot.ByTenant.ContainsKey("tenant-alpha").ShouldBeTrue();
    }

    [Test]
    public void Tenant_store_snapshot_rejects_null_entries()
    {
        AeroDBTenantStoreLocation?[] locations =
        [
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main"),
            null
        ];

        Should.Throw<ArgumentNullException>(() =>
            AeroDBTenantStoreDirectorySnapshot.Create(locations.Select(location => location!)));
    }

    [Test]
    public void Tenant_store_snapshot_is_not_affected_by_later_caller_list_mutation()
    {
        var locations = new List<AeroDBTenantStoreLocation>
        {
            new("tenant-alpha", "primary", "ca_alpha", "main")
        };

        var snapshot = AeroDBTenantStoreDirectorySnapshot.Create(locations);
        locations.Clear();

        snapshot.ByTenant.Count.ShouldBe(1);
        snapshot.ByTenant.ContainsKey("tenant-alpha").ShouldBeTrue();
    }

    [Test]
    public void Tenant_store_registry_rejects_unknown_tenants_without_a_main_store_fallback()
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest([new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main")]);

        var exception = Should.Throw<UnknownTenantIdException>(() => registry.Find("tenant-unknown"));

        exception.TenantId.ShouldBe("tenant-unknown");
    }

    [Test]
    public void Tenant_store_registry_rejects_remapping_a_tenant_after_it_is_removed_from_an_active_snapshot()
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest([new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main")]);
        registry.ActivateForTest([]);

        Should.Throw<InvalidOperationException>(() => registry.ActivateForTest(
            [new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha_remapped", "main")]));
    }

    [Test]
    public void Tenant_store_registry_rejects_reusing_a_removed_physical_location_for_another_tenant()
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest([new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main")]);
        registry.ActivateForTest([]);

        Should.Throw<InvalidOperationException>(() => registry.ActivateForTest(
            [new AeroDBTenantStoreLocation("tenant-beta", "primary", "ca_alpha", "main")]));
    }

    [Test]
    public void Tenant_store_registry_keeps_the_previous_active_snapshot_when_a_full_refresh_is_invalid()
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest([new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main")]);

        Should.Throw<InvalidOperationException>(() => registry.ActivateForTest(
        [
            new AeroDBTenantStoreLocation("tenant-beta", "primary", "ca_beta", "main"),
            new AeroDBTenantStoreLocation("tenant-gamma", "primary", "ca_beta", "main")
        ]));

        registry.Find("tenant-alpha").Namespace.ShouldBe("ca_alpha");
        Should.Throw<UnknownTenantIdException>(() => registry.Find("tenant-beta"));
    }

    [Test]
    public void Tenant_store_registry_accepts_an_exact_repeat_and_an_additive_valid_refresh()
    {
        var registry = new AeroDBTenantStoreRegistry();
        var alpha = new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main");
        registry.ActivateForTest([alpha]);

        registry.ActivateForTest(
        [
            new AeroDBTenantStoreLocation("tenant-alpha", "primary", "ca_alpha", "main"),
            new AeroDBTenantStoreLocation("tenant-beta", "primary", "ca_beta", "main")
        ]);

        registry.Find("tenant-alpha").PhysicalKey.ShouldBe(alpha.PhysicalKey);
        registry.Find("tenant-beta").Namespace.ShouldBe("ca_beta");
    }

    private static AeroDBOutboxedSessionFactory CreateFactory(
        out IDocumentStore documentStore,
        out AeroDBMessageStore messageStore)
    {
        var client = Substitute.For<ISurrealDbClient>();
        var factoryLogger = Substitute.For<ILogger<AeroDBOutboxedSessionFactory>>();

        documentStore = Substitute.For<IDocumentStore>();
        documentStore.Options.Returns(new StoreOptions());
        documentStore.Client.Returns(client);
        documentStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDocumentSession>());

        messageStore = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

        return new AeroDBOutboxedSessionFactory(documentStore, messageStore, factoryLogger);
    }

    // ─── Constructor ─────────────────────────────────────────────────

    [Test]
    public async Task Constructor_throws_when_store_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var messageStore = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);
        var logger = Substitute.For<ILogger<AeroDBOutboxedSessionFactory>>();

        Should.Throw<ArgumentNullException>(() =>
            new AeroDBOutboxedSessionFactory(null!, messageStore, logger));
    }

    [Test]
    public async Task Constructor_throws_when_messageStore_is_null()
    {
        var documentStore = Substitute.For<IDocumentStore>();
        var logger = Substitute.For<ILogger<AeroDBOutboxedSessionFactory>>();

        Should.Throw<ArgumentNullException>(() =>
            new AeroDBOutboxedSessionFactory(documentStore, null!, logger));
    }

    [Test]
    public async Task Constructor_throws_when_logger_is_null()
    {
        var documentStore = Substitute.For<IDocumentStore>();
        var client = Substitute.For<ISurrealDbClient>();
        var messageStore = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

        Should.Throw<ArgumentNullException>(() =>
            new AeroDBOutboxedSessionFactory(documentStore, messageStore, null!));
    }

    // ─── MessageStore property ──────────────────────────────────────

    [Test]
    public async Task MessageStore_exposes_backing_store()
    {
        var factory = CreateFactory(out _, out var messageStore);

        // Access internal property via InternalsVisibleTo + reflection
        var prop = typeof(AeroDBOutboxedSessionFactory)
            .GetProperty("MessageStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var exposed = prop!.GetValue(factory);
        exposed.ShouldBe(messageStore);
    }

    // ─── OpenSession(MessageContext) ─────────────────────────────────

    [Test]
    public async Task OpenSession_with_context_without_tenant_uses_default_store()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);
        // Do NOT set TenantId — it defaults to null

        var session = await factory.OpenSession(context);

        session.ShouldBe(mockSession);
        await documentStore.Received(1).OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>());
        documentStore.DidNotReceive().WithTenant(Arg.Any<string>());
    }

    [Test]
    public async Task OpenSession_with_context_with_tenant_uses_WithTenant()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var tenantStore = Substitute.For<IDocumentStore>();
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.WithTenant("tenant-alpha").Returns(tenantStore);
        tenantStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);
        context.TenantId = "tenant-alpha";

        var session = await factory.OpenSession(context);

        session.ShouldBe(mockSession);
        documentStore.Received(1).WithTenant("tenant-alpha");
    }

    // ─── OpenSession(MessageContext, string?) ───────────────────────

    [Test]
    public async Task OpenSession_with_explicit_tenant_uses_WithTenant()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var tenantStore = Substitute.For<IDocumentStore>();
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.WithTenant("tenant-beta").Returns(tenantStore);
        tenantStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);
        context.TenantId = "tenant-beta";

        var session = await factory.OpenSession(context, "tenant-beta");

        session.ShouldBe(mockSession);
        documentStore.Received(1).WithTenant("tenant-beta");
        context.TenantId.ShouldBe("tenant-beta");
    }

    [Test]
    public async Task OpenSession_with_null_tenant_uses_default_store()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);

        var session = await factory.OpenSession(context, null);

        session.ShouldBe(mockSession);
        documentStore.DidNotReceive().WithTenant(Arg.Any<string>());
    }

    [Test]
    public async Task OpenSession_with_conflicting_explicit_tenant_fails_before_opening_a_session()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);
        context.TenantId = "context-tenant";

        await Should.ThrowAsync<InvalidOperationException>(
            () => factory.OpenSession(context, "explicit-tenant"));

        documentStore.DidNotReceive().WithTenant(Arg.Any<string>());
        await documentStore.DidNotReceive().OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>());
    }

    // ─── OpenSession(IMessageBus) ───────────────────────────────────

    [Test]
    public async Task OpenSession_via_IMessageBus_delegates_to_MessageContext_overload()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);

        var session = await factory.OpenSession((IMessageBus)context);

        session.ShouldBe(mockSession);
    }

    [Test]
    public async Task OpenSession_via_IMessageBus_throws_for_non_MessageContext()
    {
        var factory = CreateFactory(out _, out _);
        var bus = Substitute.For<IMessageBus>();

        await Should.ThrowAsync<ArgumentException>(() =>
            factory.OpenSession(bus));
    }
}

public class AeroDBTenantedOutboxedSessionFactoryTests
{
    [Test]
    public async Task Missing_or_unknown_tenants_fail_closed_without_a_main_store_fallback()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);

        await Should.ThrowAsync<UnknownTenantIdException>(() => factory.OpenSessionAsync(Context(null)));
        await Should.ThrowAsync<UnknownTenantIdException>(() => factory.OpenSessionAsync(Context("unknown")));

        pairs.Calls.ShouldBe(0);
    }

    [Test]
    public async Task Each_tenant_uses_its_own_fixed_document_and_message_store_without_WithTenant()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha", "beta");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var alpha = Context("alpha");
        var beta = Context("beta");

        var alphaSession = await factory.OpenSessionAsync(alpha);
        var betaSession = await factory.OpenSessionAsync(beta);

        alphaSession.ShouldBeSameAs(pairs.Created["alpha"].Session);
        betaSession.ShouldBeSameAs(pairs.Created["beta"].Session);
        pairs.Created["alpha"].DocumentStore.DidNotReceive().WithTenant(Arg.Any<string>());
        pairs.Created["beta"].DocumentStore.DidNotReceive().WithTenant(Arg.Any<string>());
    }

    [Test]
    public async Task Routes_the_context_outbox_to_the_same_fixed_pair_as_its_document_session()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");

        await factory.OpenSessionAsync(context);

        context.Storage.ShouldBeSameAs(pairs.Created["alpha"].MessageStore);
    }

    [Test]
    public async Task Cancellation_stops_before_tenant_resolution_or_session_opening()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            factory.OpenSessionAsync(Context("alpha"), cancellation.Token));

        pairs.Calls.ShouldBe(0);
    }

    [Test]
    public async Task A_tenant_change_while_resolution_is_pending_never_routes_that_context_to_another_tenant()
    {
        var pairs = new Pairs { BlockTenant = "alpha" };
        await using var cache = Cache(pairs, "alpha", "beta");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");
        var opening = factory.OpenSessionAsync(context);
        await pairs.Entering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        context.TenantId = "beta";
        pairs.Release.SetResult();

        await Should.ThrowAsync<InvalidOperationException>(() => opening);

        pairs.Created["alpha"].DocumentStore.DidNotReceive().WithTenant(Arg.Any<string>());
        pairs.Created["alpha"].OpenCalls.ShouldBe(0);
    }

    [Test]
    public async Task A_tenant_change_while_the_session_is_opening_disposes_the_opened_session_and_fails_closed()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha", "beta");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");
        pairs.PairCreated = pair => pair.BeforeReturnSession = () => context.TenantId = "beta";

        await Should.ThrowAsync<InvalidOperationException>(() => factory.OpenSessionAsync(context));

        var alpha = pairs.Created["alpha"];
        alpha.OpenCalls.ShouldBe(1);
        await alpha.Session.Received(1).DisposeAsync();
        context.Storage.ShouldNotBeSameAs(alpha.MessageStore);
    }

    [Test]
    public async Task Cancellation_after_a_provider_ignores_the_open_token_disposes_the_session_without_mutating_the_context()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");
        var previousStorage = context.Storage;
        var previousTransaction = context.Transaction;
        var sessionOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingSession = new TaskCompletionSource<IDocumentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        pairs.PairCreated = pair =>
        {
            pair.SessionOpening = sessionOpened;
            pair.PendingSession = pendingSession;
        };
        using var cancellation = new CancellationTokenSource();

        var opening = factory.OpenSessionAsync(context, cancellation.Token);
        await sessionOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        pendingSession.SetResult(pairs.Created["alpha"].Session);

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => opening);
        exception.CancellationToken.ShouldBe(cancellation.Token);
        await pairs.Created["alpha"].Session.Received(1).DisposeAsync();
        ReferenceEquals(context.Storage, previousStorage).ShouldBeTrue();
        ReferenceEquals(context.Transaction, previousTransaction).ShouldBeTrue();
    }

    [Test]
    public async Task Persisted_inbox_queue_failure_disposes_the_session_before_the_context_storage_or_transaction_changes()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");
        var previousStorage = context.Storage;
        var previousTransaction = context.Transaction;
        context.SetEnvelope(new Envelope { Id = Guid.NewGuid(), WasPersistedInInbox = true });
        var queueFailure = new IOException("synthetic incoming queue failure");
        pairs.PairCreated = pair => pair.Session.When(session => session.QueueSqlCommand(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object[]>())).Do(_ => throw queueFailure);

        var observed = await Should.ThrowAsync<IOException>(() => factory.OpenSessionAsync(context));

        observed.ShouldBeSameAs(queueFailure);
        await pairs.Created["alpha"].Session.Received(1).DisposeAsync();
        ReferenceEquals(context.Storage, previousStorage).ShouldBeTrue();
        ReferenceEquals(context.Transaction, previousTransaction).ShouldBeTrue();
    }

    [Test]
    public async Task The_session_listener_storage_and_envelope_transaction_are_all_bound_to_the_same_fixed_pair()
    {
        var pairs = new Pairs();
        await using var cache = Cache(pairs, "alpha", "beta");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);
        var context = Context("alpha");

        var session = await factory.OpenSessionAsync(context);
        var pair = pairs.Created["alpha"];
        var listener = pair.OpenedOptions!.Listeners.Single().ShouldBeOfType<FlushOutgoingMessagesOnAeroDBCommit>();
        var envelope = new Envelope { Id = Guid.NewGuid(), MessageType = "tenant-bound", Data = [1] };

        session.ShouldBeSameAs(pair.Session);
        context.Storage.ShouldBeSameAs(pair.MessageStore);
        context.Transaction.ShouldBeOfType<AeroDBEnvelopeTransaction>();
        await context.Transaction.PersistOutgoingAsync(envelope);
        pair.Session.Received(1).QueueSqlCommand(
            "{database}",
            Arg.Is<string>(sql => sql.Contains("wolverine_outgoing_envelopes")),
            Arg.Any<object[]>());
        listener.ShouldNotBeNull();
        pairs.Created.ContainsKey("beta").ShouldBeFalse();
    }

    [Test]
    public async Task A_resolution_failure_isolated_to_one_tenant_does_not_use_or_poison_another_tenant_store()
    {
        var pairs = new Pairs { Failure = new IOException("alpha resolution failed") };
        await using var cache = Cache(pairs, "alpha", "beta");
        IAeroDBTenantedOutboxedSessionFactory factory = new AeroDBTenantedOutboxedSessionFactory(cache);

        await Should.ThrowAsync<IOException>(() => factory.OpenSessionAsync(Context("alpha")));
        pairs.Failure = null;
        var betaSession = await factory.OpenSessionAsync(Context("beta"));

        betaSession.ShouldBeSameAs(pairs.Created["beta"].Session);
        pairs.Created.ContainsKey("alpha").ShouldBeFalse();
    }

    private static AeroDBTenantStorePairCache Cache(Pairs pairs, params string[] tenantIds)
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest(tenantIds.Select(Location));
        return new AeroDBTenantStorePairCache(registry, pairs);
    }

    private static AeroDBTenantStoreLocation Location(string tenantId) =>
        new(tenantId, "primary", "ca_" + tenantId, "main");

    private static TestMessageContext Context(string? tenantId)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new TestMessageContext(runtime) { TenantId = tenantId };
        return context;
    }

    private sealed class TestMessageContext(IWolverineRuntime runtime) : MessageContext(runtime)
    {
        public void SetEnvelope(Envelope envelope) => Envelope = envelope;
    }

    private sealed class Pairs : IAeroDBTenantStorePairFactory
    {
        public Dictionary<string, Pair> Created { get; } = new(StringComparer.Ordinal);
        public Exception? Failure { get; set; }
        public string? BlockTenant { get; set; }
        public Action<Pair>? PairCreated { get; set; }
        public TaskCompletionSource Entering { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async ValueTask<IAeroDBTenantStorePair> CreateAsync(
            AeroDBTenantStoreLocation location,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(location.TenantId, BlockTenant, StringComparison.Ordinal))
            {
                Entering.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null && string.Equals(location.TenantId, "alpha", StringComparison.Ordinal))
                throw Failure;

            var pair = new Pair(location);
            Created.Add(location.TenantId, pair);
            PairCreated?.Invoke(pair);
            return pair;
        }
    }

    private sealed class Pair : IAeroDBTenantStorePair
    {
        public Pair(AeroDBTenantStoreLocation location)
        {
            Location = location;
            var client = Substitute.For<ISurrealDbClient>();
            DocumentStore = Substitute.For<IDocumentStore>();
            DocumentStore.Client.Returns(client);
            Session = Substitute.For<IDocumentSession>();
            DocumentStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    OpenCalls++;
                    OpenedOptions = call.ArgAt<SessionOptions>(0);
                    SessionOpening?.TrySetResult();
                    BeforeReturnSession?.Invoke();
                    return PendingSession?.Task ?? Task.FromResult<IDocumentSession>(Session);
                });
            MessageStore = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance,
                loggerFactory: null, storeOptions: null, ownsClient: false,
                storeIdentity: location.PhysicalIdentity);
        }

        public AeroDBTenantStoreLocation Location { get; }
        public IDocumentStore DocumentStore { get; }
        public AeroDBMessageStore MessageStore { get; }

        public AeroDBTenantMessageStore DurableMessageStore => throw new NotSupportedException();
        public IDocumentSession Session { get; }
        public int OpenCalls { get; private set; }
        public SessionOptions? OpenedOptions { get; private set; }
        public TaskCompletionSource? SessionOpening { get; set; }
        public TaskCompletionSource<IDocumentSession>? PendingSession { get; set; }
        public Action? BeforeReturnSession { get; set; }
        public ValueTask DisposeAsync() => MessageStore.DisposeAsync();
    }
}
