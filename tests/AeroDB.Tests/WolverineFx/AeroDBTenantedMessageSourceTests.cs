using AeroDB.Sable;
using AeroDB.WolverineFx;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.Tests;

public class AeroDBTenantedMessageSourceTests
{
    [Test]
    public async Task Readiness_initializes_then_migrates_a_new_store_when_Wolverine_schema_creation_is_enabled()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions
        {
            AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate
        });
        var admin = Substitute.For<IMessageStoreAdmin>();
        admin.MigrateAsync().Returns(Task.CompletedTask);
        var store = Substitute.For<IMessageStore>();
        store.Admin.Returns(admin);
        var readiness = new AeroDBTenantMessageStoreReadiness(() => runtime);

        await readiness.PrepareAsync(store);

        Received.InOrder(() =>
        {
            store.Initialize(runtime);
            _ = admin.MigrateAsync();
        });
    }

    [Test]
    public async Task Readiness_initializes_but_does_not_create_message_schema_when_Wolverine_schema_creation_is_disabled()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions
        {
            AutoBuildMessageStorageOnStartup = AutoCreate.None
        });
        var admin = Substitute.For<IMessageStoreAdmin>();
        var store = Substitute.For<IMessageStore>();
        store.Admin.Returns(admin);
        var readiness = new AeroDBTenantMessageStoreReadiness(() => runtime);

        await readiness.PrepareAsync(store);

        store.Received(1).Initialize(runtime);
        await admin.DidNotReceive().MigrateAsync();
    }

    [Test]
    public async Task Refresh_publishes_sorted_exact_stores_and_known_lookup_has_no_directory_io()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>([beta, alpha]));
        var factory = new Pairs();
        var registry = new AeroDBTenantStoreRegistry();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        ITenantedMessageSource source = Source(directory, registry, cache);

        await source.RefreshAsync();

        source.Cardinality.ShouldBe(DatabaseCardinality.DynamicMultiple);
        source.AllActive().ShouldBe(new IMessageStore[] { factory.Created["alpha"].DurableMessageStore, factory.Created["beta"].DurableMessageStore });
        source.AllActiveByTenant().Count.ShouldBe(2);
        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(new[] { "alpha", "beta" });
        source.AllActiveByTenant().Select(assignment => assignment.Value).ShouldBe(source.AllActive());
        (await source.FindAsync("alpha")).ShouldBeSameAs(factory.Created["alpha"].DurableMessageStore);
        directory.Calls.ShouldBe(1);
        factory.Calls.ShouldBe(2);
        factory.Created.Values.All(pair => pair.DisposeCalls == 0).ShouldBeTrue();
    }

    [Test]
    public async Task Unknown_lookup_refreshes_once_then_fails_without_fallback_or_pair_creation()
    {
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>([]));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);

        await Should.ThrowAsync<UnknownTenantIdException>(() => source.FindAsync("unknown").AsTask());

        directory.Calls.ShouldBe(1);
        factory.Calls.ShouldBe(0);
        source.AllActive().ShouldBeEmpty();
    }

    [Test]
    [Arguments("main")]
    [Arguments("MAIN")]
    [Arguments("default")]
    [Arguments("DEFAULT")]
    public async Task Reserved_main_and_default_selectors_are_rejected_without_directory_or_pair_access(string selector)
    {
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>([Location("alpha")]));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);

        await Should.ThrowAsync<UnknownTenantIdException>(() => source.FindAsync(selector).AsTask());

        directory.Calls.ShouldBe(0);
        factory.Calls.ShouldBe(0);
    }

    [Test]
    public async Task Directory_cannot_publish_a_tenant_for_the_coordinator_main_database()
    {
        var main = new AeroDBMainStoreIdentity("aero", "main");
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>(
            [new AeroDBTenantStoreLocation("alpha", "primary", "aero", "main")]));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache, main);

        await Should.ThrowAsync<InvalidOperationException>(() => source.RefreshAsync());

        source.AllActive().ShouldBeEmpty();
        factory.Calls.ShouldBe(0);
    }

    [Test]
    [Arguments("main")]
    [Arguments("MAIN")]
    [Arguments("default")]
    [Arguments("DEFAULT")]
    public async Task Directory_cannot_publish_reserved_Wolverine_selectors(string selector)
    {
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>(
            [new AeroDBTenantStoreLocation(selector, "primary", "ca_alpha", "main")]));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);

        await Should.ThrowAsync<InvalidOperationException>(() => source.RefreshAsync());

        source.AllActive().ShouldBeEmpty();
        factory.Calls.ShouldBe(0);
    }

    [Test]
    public async Task Refresh_adds_and_removes_active_tenants_without_disposing_cached_pairs()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        active = [alpha, beta];

        (await source.FindAsync("beta")).ShouldBeSameAs(factory.Created["beta"].DurableMessageStore);
        active = [beta];
        await source.RefreshLiteAsync();

        source.AllActive().ShouldBe(new IMessageStore[] { factory.Created["beta"].DurableMessageStore });
        await Should.ThrowAsync<UnknownTenantIdException>(() => source.FindAsync("alpha").AsTask());
        Should.Throw<UnknownTenantIdException>(() => registry.Find("alpha"));
        factory.Calls.ShouldBe(2);
        factory.Created.Values.All(pair => pair.DisposeCalls == 0).ShouldBeTrue();
    }

    [Test]
    [Arguments("duplicate-tenant")]
    [Arguments("duplicate-physical")]
    [Arguments("remap")]
    [Arguments("null-entry")]
    [Arguments("null-directory")]
    public async Task Invalid_directory_preserves_previous_complete_snapshot_and_registry(string kind)
    {
        var alpha = Location("alpha");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        var previous = source.AllActive();
        active = kind switch
        {
            "duplicate-tenant" => [alpha, alpha],
            "duplicate-physical" => [alpha, new("beta", alpha.ClusterAlias, alpha.Namespace, alpha.Database)],
            "remap" => [new("alpha", alpha.ClusterAlias, "another_namespace", alpha.Database)],
            "null-entry" => [alpha, null!],
            _ => null!
        };

        if (kind.StartsWith("null", StringComparison.Ordinal))
            await Should.ThrowAsync<ArgumentNullException>(() => source.RefreshAsync());
        else
            await Should.ThrowAsync<InvalidOperationException>(() => source.RefreshAsync());

        source.AllActive().ShouldBe(previous);
        registry.Find("alpha").ShouldBe(alpha);
        factory.Calls.ShouldBe(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Failed_materialization_rolls_back_active_registry_and_source_without_partial_publication(bool lite)
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        active = [alpha, beta];
        var failure = new IOException("synthetic pair failure");
        factory.Failure = failure;

        var observed = await Should.ThrowAsync<IOException>(() => lite ? source.RefreshLiteAsync() : source.RefreshAsync());

        observed.ShouldBeSameAs(failure);
        source.AllActive().ShouldBe(new IMessageStore[] { factory.Created["alpha"].DurableMessageStore });
        registry.Find("alpha").ShouldBe(alpha);
        Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
        (await source.FindAsync("alpha")).ShouldBeSameAs(factory.Created["alpha"].DurableMessageStore);
        factory.Failure = null;
        await source.RefreshAsync();
        (await source.FindAsync("beta")).ShouldBeSameAs(factory.Created["beta"].DurableMessageStore);
    }

    [Test]
    public async Task Concurrent_refreshes_are_serialized_and_do_not_expose_an_incomplete_snapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = new Directory(async call =>
        {
            if (call == 1) { entered.SetResult(); await release.Task; }
            return new[] { Location("alpha") };
        });
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        var first = source.RefreshAsync();
        if (first.IsFaulted) await first;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = source.RefreshAsync();

        directory.Calls.ShouldBe(1);
        source.AllActive().ShouldBeEmpty();
        release.SetResult();
        await Task.WhenAll(first, second);

        directory.MaximumConcurrentCalls.ShouldBe(1);
        directory.Calls.ShouldBe(2);
        factory.Calls.ShouldBe(1);
        source.AllActive().Count.ShouldBe(1);
    }

    [Test]
    public async Task Directory_failure_preserves_snapshot_and_original_failure()
    {
        var failure = new IOException("synthetic directory failure");
        var directory = new Directory(call => call == 1
            ? Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>([Location("alpha")])
            : Task.FromException<IReadOnlyList<AeroDBTenantStoreLocation>>(failure));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();

        (await Should.ThrowAsync<IOException>(() => source.RefreshAsync())).ShouldBeSameAs(failure);

        registry.Find("alpha").ShouldBe(Location("alpha"));
        source.AllActive().Count.ShouldBe(1);
        factory.Calls.ShouldBe(1);
    }

    [Test]
    public async Task Concurrent_lookup_misses_share_one_successful_directory_generation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = new Directory(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return new[] { Location("alpha") };
        });
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        var first = source.FindAsync("alpha").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 7).Select(_ => source.FindAsync("alpha").AsTask()).ToArray();
        release.SetResult();

        var results = await Task.WhenAll(others.Append(first));

        results.All(store => ReferenceEquals(store, factory.Created["alpha"].DurableMessageStore)).ShouldBeTrue();
        directory.Calls.ShouldBe(1);
        factory.Calls.ShouldBe(1);
    }

    [Test]
    public async Task Blocked_candidate_materialization_exposes_only_the_previous_complete_snapshot()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        var previous = source.AllActive();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.BeforeCreate = async () => { entered.SetResult(); await release.Task; };
        active = [alpha, beta];
        var refresh = source.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            source.AllActive().ShouldBe(previous);
            source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(new[] { "alpha" });
            (await source.FindAsync("alpha")).ShouldBeSameAs(factory.Created["alpha"].DurableMessageStore);
            // Candidate resources may be built privately, but ordinary registry
            // and cache callers must not observe or acquire an unactivated route.
            Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
            await Should.ThrowAsync<UnknownTenantIdException>(() => cache.GetAsync("beta").AsTask());
        }
        finally { release.TrySetResult(); }
        await refresh;

        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(new[] { "alpha", "beta" });
        var stores = source.AllActive().ShouldBeAssignableTo<IList<IMessageStore>>();
        Should.Throw<NotSupportedException>(() => stores[0] = factory.Created["beta"].DurableMessageStore);
        (await source.FindAsync("alpha")).ShouldBeSameAs(factory.Created["alpha"].DurableMessageStore);
    }

    [Test]
    public async Task Failed_candidate_keeps_historical_location_reserved_against_later_remap()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        active = [alpha, beta];
        factory.Failure = new IOException("synthetic candidate failure");
        await Should.ThrowAsync<IOException>(() => source.RefreshAsync());
        factory.Failure = null;
        active = [alpha, new("beta", "primary", "remapped_namespace", "main")];

        await Should.ThrowAsync<InvalidOperationException>(() => source.RefreshAsync());

        registry.Find("alpha").ShouldBe(alpha);
        Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
        source.AllActive().Count.ShouldBe(1);
        factory.Calls.ShouldBe(2);
    }

    [Test]
    public async Task Staged_cache_builds_privately_and_activation_reuses_the_same_pair()
    {
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var stage = registry.Stage([Location("alpha")]);

        var staged = await cache.GetStagedAsync(stage, "alpha");

        await Should.ThrowAsync<UnknownTenantIdException>(() => cache.GetAsync("alpha").AsTask());
        var foreign = new AeroDBTenantStoreRegistry().Stage([Location("beta")]);
        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetStagedAsync(foreign, "beta").AsTask());
        factory.Calls.ShouldBe(1);
        registry.Activate(stage);
        (await cache.GetAsync("alpha")).ShouldBeSameAs(staged);
        factory.Calls.ShouldBe(1);
    }

    [Test]
    public async Task Superseded_stage_cannot_activate_a_complete_but_stale_source_view()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache);
        await source.RefreshAsync();
        active = [alpha, beta];
        factory.BeforeCreate = () =>
        {
            registry.Stage([alpha]);
            return Task.CompletedTask;
        };

        await Should.ThrowAsync<InvalidOperationException>(() => source.RefreshAsync());

        registry.Find("alpha").ShouldBe(alpha);
        Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(new[] { "alpha" });
        factory.Calls.ShouldBe(2);
        factory.Created.Values.All(pair => pair.DisposeCalls == 0).ShouldBeTrue();
    }

    [Test]
    public async Task Refresh_prepares_every_new_pair_before_publishing_the_complete_snapshot()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var directory = new Directory(_ => Task.FromResult<IReadOnlyList<AeroDBTenantStoreLocation>>([alpha, beta]));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        var readiness = new RecordingReadiness(tenantId =>
        {
            Should.Throw<UnknownTenantIdException>(() => registry.Find(tenantId));
            return Task.CompletedTask;
        });
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache, readiness: readiness);

        await source.RefreshAsync();

        readiness.Prepared.ShouldBe(["alpha", "beta"]);
        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(["alpha", "beta"]);
        registry.Find("alpha").ShouldBe(alpha);
        registry.Find("beta").ShouldBe(beta);
    }

    [Test]
    public async Task Failed_second_pair_preparation_preserves_the_previous_snapshot_until_a_retry_completes()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var gamma = Location("gamma");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        var readiness = new RecordingReadiness();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache, readiness: readiness);
        await source.RefreshAsync();
        active = [alpha, beta, gamma];
        var failure = new IOException("synthetic second-pair preparation failure");
        readiness.FailureFor = "gamma";
        readiness.Failure = failure;

        (await Should.ThrowAsync<IOException>(() => source.RefreshAsync())).ShouldBeSameAs(failure);

        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(["alpha"]);
        registry.Find("alpha").ShouldBe(alpha);
        Should.Throw<UnknownTenantIdException>(() => registry.Find("beta"));
        Should.Throw<UnknownTenantIdException>(() => registry.Find("gamma"));
        readiness.Failure = null;
        readiness.FailureFor = null;
        await source.RefreshAsync();
        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(["alpha", "beta", "gamma"]);
        readiness.Prepared.ShouldBe(["alpha", "beta", "gamma", "beta", "gamma"]);
    }

    [Test]
    public async Task Refresh_reuses_ready_active_pairs_without_repreparing_them_but_prepares_a_readded_tenant()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        IReadOnlyList<AeroDBTenantStoreLocation> active = [alpha];
        var directory = new Directory(_ => Task.FromResult(active));
        var registry = new AeroDBTenantStoreRegistry();
        var factory = new Pairs();
        var readiness = new RecordingReadiness();
        await using var cache = new AeroDBTenantStorePairCache(registry, factory);
        var source = Source(directory, registry, cache, readiness: readiness);

        await source.RefreshAsync();
        await source.RefreshAsync();
        active = [alpha, beta];
        await source.RefreshLiteAsync();
        active = [beta];
        await source.RefreshAsync();
        active = [alpha, beta];
        await source.RefreshAsync();

        readiness.Prepared.ShouldBe(["alpha", "beta", "alpha"]);
        factory.Calls.ShouldBe(2);
        source.AllActiveByTenant().Select(assignment => assignment.TenantId).ShouldBe(["alpha", "beta"]);
    }

    [Test]
    public void Existing_single_store_registration_does_not_activate_the_dormant_tenant_source()
    {
        var options = new WolverineOptions();
        options.PersistMessagesWithAeroDB(Substitute.For<ISurrealDbClient>(), NullLogger<AeroDBMessageStore>.Instance);
        options.Services.ShouldNotContain(descriptor =>
            descriptor.ServiceType == typeof(ITenantedMessageSource)
            || descriptor.ServiceType == typeof(AeroDBTenantStorePairCache)
            || descriptor.ServiceType == typeof(IAeroDBTenantStorePairFactory)
            || descriptor.ServiceType == typeof(IAeroDBTenantedOutboxedSessionFactory)
            || descriptor.ServiceType == typeof(MultiTenantedMessageStore));
    }

    private static AeroDBTenantStoreLocation Location(string tenant) => new(tenant, "primary", "ca_" + tenant, "main");

    private static AeroDBTenantedMessageSource Source(
        IAeroDBTenantStoreDirectory directory,
        AeroDBTenantStoreRegistry registry,
        AeroDBTenantStorePairCache cache,
        AeroDBMainStoreIdentity? main = null,
        IAeroDBTenantMessageStoreReadiness? readiness = null) =>
        new(directory, registry, cache, main, readiness ?? new RecordingReadiness());

    private sealed class Directory(Func<int, Task<IReadOnlyList<AeroDBTenantStoreLocation>>> list) : IAeroDBTenantStoreDirectory
    {
        private int _calls;
        private int _active;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrentCalls { get; private set; }
        public async ValueTask<IReadOnlyList<AeroDBTenantStoreLocation>> ListActiveAsync(CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try { return await list(Interlocked.Increment(ref _calls)); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class Pairs : IAeroDBTenantStorePairFactory
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Func<Task>? BeforeCreate { get; set; }
        public Dictionary<string, Pair> Created { get; } = new(StringComparer.Ordinal);
        public async ValueTask<IAeroDBTenantStorePair> CreateAsync(AeroDBTenantStoreLocation location, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (BeforeCreate is not null) await BeforeCreate();
            var pair = new Pair(location);
            Created.Add(location.TenantId, pair);
            return pair;
        }
    }

    private sealed class RecordingReadiness(Func<string, Task>? onPrepare = null)
        : IAeroDBTenantMessageStoreReadiness
    {
        public List<string> Prepared { get; } = [];
        public string? FailureFor { get; set; }
        public Exception? Failure { get; set; }

        public Task PrepareAsync(IMessageStore store, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = store.TenantIds.Single();
            Prepared.Add(tenantId);
            if (string.Equals(tenantId, FailureFor, StringComparison.Ordinal) && Failure is not null)
                return Task.FromException(Failure);
            return onPrepare?.Invoke(tenantId) ?? Task.CompletedTask;
        }
    }

    private sealed class Pair : IAeroDBTenantStorePair
    {
        public Pair(AeroDBTenantStoreLocation location)
        {
            Location = location;
            DocumentStore = Substitute.For<IDocumentStore>();
            MessageStore = new AeroDBMessageStore(Substitute.For<ISurrealDbClient>(),
                NullLogger<AeroDBMessageStore>.Instance, null, null, ownsClient: false,
                storeIdentity: location.PhysicalIdentity);
            MessageStore.TenantIds.Add(location.TenantId);
            DurableMessageStore = new AeroDBTenantMessageStore(location, MessageStore);
        }
        public AeroDBTenantStoreLocation Location { get; }
        public IDocumentStore DocumentStore { get; }
        public AeroDBMessageStore MessageStore { get; }
        public AeroDBTenantMessageStore DurableMessageStore { get; }
        public int DisposeCalls { get; private set; }
        public ValueTask DisposeAsync() { DisposeCalls++; return MessageStore.DisposeAsync(); }
    }
}
