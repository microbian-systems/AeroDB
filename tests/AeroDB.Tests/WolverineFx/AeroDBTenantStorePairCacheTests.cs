using AeroDB.Sable;
using AeroDB.WolverineFx;
using System.Collections.Concurrent;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;

namespace AeroDB.Tests;

public class AeroDBTenantStorePairCacheTests
{
    [Test]
    public async Task Unknown_tenants_fail_before_any_pair_factory_call()
    {
        var factory = new FakePairFactory((_, _) =>
            throw new InvalidOperationException("The factory must not run for an unknown tenant."));
        var cache = new AeroDBTenantStorePairCache(new AeroDBTenantStoreRegistry(), factory);

        var exception = await Should.ThrowAsync<UnknownTenantIdException>(
            () => cache.GetAsync("unknown-tenant").AsTask());

        exception.TenantId.ShouldBe("unknown-tenant");
        factory.TotalCalls.ShouldBe(0);
    }

    [Test]
    public async Task Concurrent_calls_for_one_tenant_create_one_pair_for_its_physical_store()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var registry = Registry(location);
        var pair = Pair(location);
        var creation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePairFactory((_, _) => new ValueTask<IAeroDBTenantStorePair>(creation.Task));
        var cache = new AeroDBTenantStorePairCache(registry, factory);

        var requests = Enumerable.Range(0, 16)
            .Select(_ => cache.GetAsync(location.TenantId).AsTask())
            .ToArray();

        requests.All(task => !task.IsCompleted).ShouldBeTrue();
        factory.CallsFor(location).ShouldBe(1);

        creation.SetResult(pair);
        var pairs = await Task.WhenAll(requests);

        pairs.All(returned => ReferenceEquals(pair, returned)).ShouldBeTrue();
    }

    [Test]
    public async Task Failed_pair_creation_is_evicted_without_affecting_another_healthy_tenant()
    {
        var alpha = Location("tenant-alpha", "ca_alpha");
        var beta = Location("tenant-beta", "ca_beta");
        var registry = Registry(alpha, beta);
        var alphaPair = Pair(alpha);
        var betaPair = Pair(beta);
        var alphaAttempts = 0;
        var factory = new FakePairFactory((location, _) =>
        {
            if (location == alpha)
            {
                alphaAttempts++;
                return alphaAttempts == 1
                    ? ValueTask.FromException<IAeroDBTenantStorePair>(
                        new InvalidOperationException("Simulated alpha factory failure."))
                    : ValueTask.FromResult<IAeroDBTenantStorePair>(alphaPair);
            }

            return ValueTask.FromResult<IAeroDBTenantStorePair>(betaPair);
        });
        var cache = new AeroDBTenantStorePairCache(registry, factory);

        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetAsync(alpha.TenantId).AsTask());
        factory.CallsFor(alpha).ShouldBe(1);
        (await cache.GetAsync(beta.TenantId)).ShouldBeSameAs(betaPair);
        (await cache.GetAsync(alpha.TenantId)).ShouldBeSameAs(alphaPair);
        (await cache.GetAsync(beta.TenantId)).ShouldBeSameAs(betaPair);

        factory.CallsFor(alpha).ShouldBe(2);
        factory.CallsFor(beta).ShouldBe(1);
    }

    [Test]
    public async Task Caller_cancellation_does_not_cancel_shared_pair_creation()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var creation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePairFactory((_, _) => new ValueTask<IAeroDBTenantStorePair>(creation.Task));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);
        using var cancellation = new CancellationTokenSource();

        var cancelledRequest = cache.GetAsync(location.TenantId, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => cancelledRequest);
        factory.CallsFor(location).ShouldBe(1);

        var survivingRequest = cache.GetAsync(location.TenantId).AsTask();
        var pair = Pair(location);
        creation.SetResult(pair);

        (await survivingRequest).ShouldBeSameAs(pair);
        factory.CallsFor(location).ShouldBe(1);
    }

    [Test]
    public async Task Factory_cancellation_is_evicted_so_a_later_request_can_create_a_pair()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var pair = Pair(location);
        var attempts = 0;
        var factory = new FakePairFactory((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? ValueTask.FromCanceled<IAeroDBTenantStorePair>(new CancellationToken(canceled: true))
                : ValueTask.FromResult<IAeroDBTenantStorePair>(pair);
        });
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);

        await Should.ThrowAsync<OperationCanceledException>(() => cache.GetAsync(location.TenantId).AsTask());

        factory.CallsFor(location).ShouldBe(1);
        (await cache.GetAsync(location.TenantId)).ShouldBeSameAs(pair);
        factory.CallsFor(location).ShouldBe(2);
    }

    [Test]
    public async Task Coincident_cancelled_callers_do_not_leave_a_cancelled_shared_factory_task_cached()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var factoryCancellation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = Pair(location);
        var attempts = 0;
        var factory = new FakePairFactory((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? new ValueTask<IAeroDBTenantStorePair>(factoryCancellation.Task)
                : ValueTask.FromResult<IAeroDBTenantStorePair>(pair);
        });
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);
        using var callerCancellation = new CancellationTokenSource();

        var first = cache.GetAsync(location.TenantId, callerCancellation.Token).AsTask();
        var second = cache.GetAsync(location.TenantId, callerCancellation.Token).AsTask();
        callerCancellation.Cancel();
        factoryCancellation.SetCanceled();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        await Should.ThrowAsync<OperationCanceledException>(() => second);

        (await cache.GetAsync(location.TenantId)).ShouldBeSameAs(pair);
        factory.CallsFor(location).ShouldBe(2);
    }

    [Test]
    public async Task Factory_pairs_with_a_different_location_are_disposed_and_rejected()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var wrongPair = Pair(Location("tenant-alpha", "ca_wrong"));
        var factory = new FakePairFactory((_, _) => ValueTask.FromResult<IAeroDBTenantStorePair>(wrongPair));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);

        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetAsync(location.TenantId).AsTask());

        wrongPair.DisposeCalls.ShouldBe(1);
        factory.CallsFor(location).ShouldBe(1);
    }

    [Test]
    public async Task Factory_null_pairs_are_rejected()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var factory = new FakePairFactory((_, _) =>
            ValueTask.FromResult<IAeroDBTenantStorePair>(null!));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);

        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetAsync(location.TenantId).AsTask());
    }

    [Test]
    public async Task Pair_disposes_the_borrowed_message_store_without_releasing_the_document_store_client()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var documentStore = Substitute.For<IDocumentStore>();
        var client = Substitute.For<ISurrealDbClient>();
        documentStore.Client.Returns(client);
        var messageStore = new AeroDBMessageStore(
            client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: null,
            ownsClient: false);
        var pair = new AeroDBTenantStorePair(location, documentStore, messageStore);

        await pair.DisposeAsync();
        await pair.DisposeAsync();

        messageStore.HasDisposed.ShouldBeTrue();
        await documentStore.Received(1).DisposeAsync();
        await client.DidNotReceive().DisposeAsync();
    }

    [Test]
    public void Pair_rejects_a_message_store_that_owns_the_document_store_client()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var client = Substitute.For<ISurrealDbClient>();
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Client.Returns(client);
        var owningMessageStore = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);

        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStorePair(location, documentStore, owningMessageStore));
    }

    [Test]
    public void Pair_rejects_a_message_store_with_a_different_client_than_the_document_store()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var documentStoreClient = Substitute.For<ISurrealDbClient>();
        var messageStoreClient = Substitute.For<ISurrealDbClient>();
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Client.Returns(documentStoreClient);
        var borrowedMessageStore = new AeroDBMessageStore(
            messageStoreClient,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: null,
            ownsClient: false);

        Should.Throw<ArgumentException>(() =>
            new AeroDBTenantStorePair(location, documentStore, borrowedMessageStore));
    }

    [Test]
    public async Task Cached_pairs_are_disposed_once_when_concurrent_shutdown_begins()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var registry = Registry(location);
        var pair = Pair(location);
        var factory = new FakePairFactory((_, _) => ValueTask.FromResult<IAeroDBTenantStorePair>(pair));
        var cache = new AeroDBTenantStorePairCache(registry, factory);

        (await cache.GetAsync(location.TenantId)).ShouldBeSameAs(pair);
        var shutdowns = Enumerable.Range(0, 16)
            .Select(_ => cache.DisposeAsync().AsTask())
            .ToArray();

        await Task.WhenAll(shutdowns);

        pair.DisposeCalls.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(() => cache.GetAsync(location.TenantId).AsTask());
    }

    [Test]
    public async Task Shutdown_drains_an_inflight_pair_then_prevents_new_requests()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var pair = Pair(location);
        var creation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePairFactory((_, _) => new ValueTask<IAeroDBTenantStorePair>(creation.Task));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);

        var pendingRequest = cache.GetAsync(location.TenantId).AsTask();
        var shutdown = cache.DisposeAsync().AsTask();

        await Should.ThrowAsync<ObjectDisposedException>(() => cache.GetAsync(location.TenantId).AsTask());
        creation.SetResult(pair);

        (await pendingRequest).ShouldBeSameAs(pair);
        await shutdown;
        pair.DisposeCalls.ShouldBe(1);
    }

    [Test]
    public async Task Faulted_pair_disposal_is_shared_without_a_retry()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var expected = new InvalidOperationException("Simulated pair disposal failure.");
        var pair = Pair(location, () => ValueTask.FromException(expected));
        var factory = new FakePairFactory((_, _) => ValueTask.FromResult<IAeroDBTenantStorePair>(pair));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);
        await cache.GetAsync(location.TenantId);

        var shutdowns = Enumerable.Range(0, 16)
            .Select(_ => cache.DisposeAsync().AsTask())
            .ToArray();

        shutdowns.Skip(1).All(task => ReferenceEquals(shutdowns[0], task)).ShouldBeTrue();
        var failures = await Task.WhenAll(shutdowns.Select(async task =>
            await Should.ThrowAsync<InvalidOperationException>(() => task)));
        foreach (var failure in failures)
            ReferenceEquals(expected, failure).ShouldBeTrue();

        ReferenceEquals(shutdowns[0], cache.DisposeAsync().AsTask()).ShouldBeTrue();
        pair.DisposeCalls.ShouldBe(1);
    }

    [Test]
    public async Task Concurrent_waiters_receive_the_same_factory_failure_without_hidden_retry()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var creation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("Synthetic initializer failure.");
        var pair = Pair(location);
        var attempts = 0;
        var factory = new FakePairFactory((_, _) =>
            ++attempts == 1
                ? new ValueTask<IAeroDBTenantStorePair>(creation.Task)
                : ValueTask.FromResult<IAeroDBTenantStorePair>(pair));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);
        var waiters = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(location.TenantId).AsTask()).ToArray();

        creation.SetException(expected);
        foreach (var waiter in waiters)
        {
            var failure = await Should.ThrowAsync<InvalidOperationException>(() => waiter);
            failure.ShouldBeSameAs(expected);
        }
        factory.CallsFor(location).ShouldBe(1);
        (await cache.GetAsync(location.TenantId)).ShouldBeSameAs(pair);
        factory.CallsFor(location).ShouldBe(2);
        await cache.DisposeAsync();
        pair.DisposeCalls.ShouldBe(1);
    }

    [Test]
    public async Task Late_factory_failure_after_all_waiters_cancel_is_evicted_for_the_next_request()
    {
        var location = Location("tenant-alpha", "ca_alpha");
        var creation = new TaskCompletionSource<IAeroDBTenantStorePair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = Pair(location);
        var attempts = 0;
        var factory = new FakePairFactory((_, _) =>
            ++attempts == 1
                ? new ValueTask<IAeroDBTenantStorePair>(creation.Task)
                : ValueTask.FromResult<IAeroDBTenantStorePair>(pair));
        var cache = new AeroDBTenantStorePairCache(Registry(location), factory);
        using var cancellation = new CancellationTokenSource();
        var waiting = cache.GetAsync(location.TenantId, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        creation.SetException(new InvalidOperationException("Synthetic late initializer failure."));
        // Await the shared initializer's completion without timing sleeps.
        await Should.ThrowAsync<InvalidOperationException>(() => creation.Task);
        try
        {
            await cache.GetAsync(location.TenantId);
        }
        catch (InvalidOperationException)
        {
            // A request racing the eviction may observe the old failure once.
        }
        (await cache.GetAsync(location.TenantId)).ShouldBeSameAs(pair);
        factory.CallsFor(location).ShouldBe(2);
        await cache.DisposeAsync();
        pair.DisposeCalls.ShouldBe(1);
    }

    private static AeroDBTenantStoreLocation Location(string tenantId, string @namespace) =>
        new(tenantId, "primary", @namespace, "main");

    private static AeroDBTenantStoreRegistry Registry(params AeroDBTenantStoreLocation[] locations)
    {
        var registry = new AeroDBTenantStoreRegistry();
        registry.ActivateForTest(locations);
        return registry;
    }

    private static FakePair Pair(
        AeroDBTenantStoreLocation location,
        Func<ValueTask>? dispose = null) => new(location, dispose);

    private sealed class FakePairFactory : IAeroDBTenantStorePairFactory
    {
        private readonly Func<AeroDBTenantStoreLocation, CancellationToken, ValueTask<IAeroDBTenantStorePair>> _create;
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public FakePairFactory(
            Func<AeroDBTenantStoreLocation, CancellationToken, ValueTask<IAeroDBTenantStorePair>> create)
        {
            _create = create;
        }

        public int TotalCalls => _calls.Values.Sum();

        public int CallsFor(AeroDBTenantStoreLocation location) =>
            _calls.TryGetValue(location.PhysicalKey, out var count) ? count : 0;

        public ValueTask<IAeroDBTenantStorePair> CreateAsync(
            AeroDBTenantStoreLocation location,
            CancellationToken cancellationToken = default)
        {
            _calls.AddOrUpdate(location.PhysicalKey, 1, static (_, count) => count + 1);
            return _create(location, cancellationToken);
        }
    }

    private sealed class FakePair : IAeroDBTenantStorePair
    {
        private int _disposeCalls;

        private readonly Func<ValueTask> _dispose;

        public FakePair(AeroDBTenantStoreLocation location, Func<ValueTask>? dispose = null)
        {
            Location = location;
            _dispose = dispose ?? (() => ValueTask.CompletedTask);
        }

        public AeroDBTenantStoreLocation Location { get; }

        public IDocumentStore DocumentStore => throw new NotSupportedException();

        public AeroDBMessageStore MessageStore => throw new NotSupportedException();

        public AeroDBTenantMessageStore DurableMessageStore => throw new NotSupportedException();

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return _dispose();
        }
    }
}
