using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine.Persistence.Durability;

namespace AeroDB.Tests;

public class AeroDBTenantStorePairFactoryTests
{
    [Test]
    public async Task Creation_binds_exact_location_without_schema_or_mutable_tenant_selection()
    {
        var location = Location("alpha");
        var document = Document(location);
        var documents = Factory(document);
        using var cancellation = new CancellationTokenSource();
        var factory = PairFactory(documents);

        await using var pair = await factory.CreateAsync(location, cancellation.Token);

        pair.Location.ShouldBe(location);
        pair.DocumentStore.ShouldBeSameAs(document);
        pair.MessageStore.Client.ShouldBeSameAs(document.Client);
        pair.MessageStore.OwnsClient.ShouldBeFalse();
        pair.MessageStore.Role.ShouldBe(MessageStoreRole.Ancillary);
        pair.MessageStore.TenantIds.ShouldBe(new[] { location.TenantId });
        pair.MessageStore.Uri.ShouldBe(location.PhysicalIdentity);
        pair.MessageStore.Uri.AbsoluteUri.ShouldNotContain("credentials-canary");
        await documents.Received(1).CreateAsync(location, cancellation.Token);
        await document.Received(1).InitializeAsync(cancellation.Token);
        document.DidNotReceiveWithAnyArgs().WithTenant(default!);
        await document.Client.DidNotReceiveWithAnyArgs().CreateSession(default);
        await document.Client.DidNotReceiveWithAnyArgs().RawQuery(default!, default!, default);
    }

    [Test]
    public async Task Null_store_and_precanceled_creation_fail_without_initialization()
    {
        var documents = Substitute.For<IAeroDBTenantDocumentStoreFactory>();
        documents.CreateAsync(Arg.Any<AeroDBTenantStoreLocation>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IDocumentStore>(null!));
        var factory = PairFactory(documents);

        await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateAsync(Location("alpha")).AsTask());
        documents.ClearReceivedCalls();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            factory.CreateAsync(Location("alpha"), cancellation.Token).AsTask());
        exception.CancellationToken.ShouldBe(cancellation.Token);
        await documents.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Test]
    [Arguments("namespace")]
    [Arguments("database")]
    [Arguments("tenancy")]
    [Arguments("default-tenant")]
    public async Task Wrong_or_mutable_scope_is_rejected_before_initialization_and_disposed(string kind)
    {
        var location = Location("alpha");
        var document = Document(location);
        switch (kind)
        {
            case "namespace": document.Options.Namespace = "another_namespace"; break;
            case "database": document.Options.Database = "another_database"; break;
            case "tenancy": document.Options.TenancyStyle = TenancyStyle.DatabasePerTenant; break;
            case "default-tenant": document.Options.DefaultTenantId = "another_tenant"; break;
        }

        await Should.ThrowAsync<InvalidOperationException>(() =>
            PairFactory(Factory(document)).CreateAsync(location).AsTask());

        await document.DidNotReceiveWithAnyArgs().InitializeAsync(default);
        await document.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Initialization_failure_preserves_original_exception_and_releases_returned_store()
    {
        var location = Location("alpha");
        var document = Document(location);
        var failure = new InvalidOperationException("synthetic initialization failure");
        document.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));

        var observed = await Should.ThrowAsync<InvalidOperationException>(() =>
            PairFactory(Factory(document)).CreateAsync(location).AsTask());

        observed.ShouldBeSameAs(failure);
        await document.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Initialization_cancellation_preserves_token_and_releases_returned_store()
    {
        var location = Location("alpha");
        var document = Document(location);
        using var cancellation = new CancellationTokenSource();
        document.InitializeAsync(cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });

        var observed = await Should.ThrowAsync<OperationCanceledException>(() =>
            PairFactory(Factory(document)).CreateAsync(location, cancellation.Token).AsTask());

        observed.CancellationToken.ShouldBe(cancellation.Token);
        await document.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Scope_drift_during_initialization_is_rejected_and_disposed()
    {
        var location = Location("alpha");
        var document = Document(location);
        document.InitializeAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            document.Options.Database = "changed_during_initialization";
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            PairFactory(Factory(document)).CreateAsync(location).AsTask());

        await document.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Canonical_same_endpoint_is_accepted_but_cluster_remap_is_rejected()
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var gamma = Location("gamma");
        var first = Document(alpha, "HTTP://LOCALHOST:8000");
        var second = Document(beta, "http://localhost:8000/");
        var rejected = Document(gamma, "http://another-host:8000/");
        var documents = Substitute.For<IAeroDBTenantDocumentStoreFactory>();
        documents.CreateAsync(alpha, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(first));
        documents.CreateAsync(beta, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(second));
        documents.CreateAsync(gamma, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(rejected));
        var factory = PairFactory(documents);

        await using var firstPair = await factory.CreateAsync(alpha);
        await using var secondPair = await factory.CreateAsync(beta);
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateAsync(gamma).AsTask());

        failure.Message.ShouldNotContain("another-host");
        await rejected.DidNotReceiveWithAnyArgs().InitializeAsync(default);
        await rejected.Received(1).DisposeAsync();
        firstPair.MessageStore.Uri.ShouldNotBe(secondPair.MessageStore.Uri);
    }

    [Test]
    [Arguments("http://credentials-canary:password-canary@localhost:8000")]
    [Arguments("http://localhost:8000/?token=token-canary")]
    [Arguments("http://localhost:8000/#fragment-canary")]
    [Arguments("not-an-absolute-endpoint-canary")]
    public async Task Ambiguous_or_secret_bearing_endpoints_fail_with_value_free_diagnostics(string endpoint)
    {
        var location = Location("alpha");
        var document = Document(location, endpoint);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            PairFactory(Factory(document)).CreateAsync(location).AsTask());

        failure.Message.ShouldNotContain("canary");
        failure.Message.ShouldNotContain(endpoint);
        await document.DidNotReceiveWithAnyArgs().InitializeAsync(default);
        await document.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Pair_disposal_releases_document_once_without_direct_client_disposal()
    {
        var location = Location("alpha");
        var document = Document(location);
        var pair = await PairFactory(Factory(document)).CreateAsync(location);

        await Task.WhenAll(pair.DisposeAsync().AsTask(), pair.DisposeAsync().AsTask());

        pair.MessageStore.HasDisposed.ShouldBeTrue();
        await document.Received(1).DisposeAsync();
        await ((IAsyncDisposable)document.Client).DidNotReceive().DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reused_store_or_client_is_rejected_without_disposing_the_healthy_pair(bool shareClientOnly)
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var first = Document(alpha);
        var rejected = shareClientOnly ? Document(beta) : first;
        if (shareClientOnly)
        {
            var sharedClient = first.Client;
            rejected.Client.Returns(sharedClient);
            rejected.Client.ShouldBeSameAs(first.Client);
        }
        var documents = Substitute.For<IAeroDBTenantDocumentStoreFactory>();
        documents.CreateAsync(alpha, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(first));
        documents.CreateAsync(beta, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(rejected));
        var factory = PairFactory(documents);
        await using var pair = await factory.CreateAsync(alpha);

        await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateAsync(beta).AsTask());

        pair.MessageStore.HasDisposed.ShouldBeFalse();
        await first.DidNotReceive().DisposeAsync();
        await rejected.DidNotReceive().DisposeAsync();
        await ((IAsyncDisposable)first.Client).DidNotReceive().DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Concurrent_cluster_binding_accepts_equivalent_endpoints_and_rejects_conflicting_ones(bool conflicting)
    {
        var alpha = Location("alpha");
        var beta = Location("beta");
        var first = Document(alpha);
        var second = Document(beta, conflicting ? "http://another-host:8000/" : "HTTP://LOCALHOST:8000/");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var documents = Substitute.For<IAeroDBTenantDocumentStoreFactory>();
        documents.CreateAsync(Arg.Any<AeroDBTenantStoreLocation>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IDocumentStore>(ReturnAfterBarrier(
                call.Arg<AeroDBTenantStoreLocation>() == alpha ? first : second)));
        async Task<IDocumentStore> ReturnAfterBarrier(IDocumentStore document)
        {
            await barrier.Task;
            return document;
        }
        var factory = PairFactory(documents);
        var attempts = new[] { factory.CreateAsync(alpha).AsTask(), factory.CreateAsync(beta).AsTask() };
        barrier.SetResult();
        try { await Task.WhenAll(attempts); }
        catch (InvalidOperationException) when (conflicting) { }

        attempts.Count(task => task.IsCompletedSuccessfully).ShouldBe(conflicting ? 1 : 2);
        attempts.Count(task => task.IsFaulted).ShouldBe(conflicting ? 1 : 0);
        foreach (var task in attempts.Where(task => task.IsCompletedSuccessfully))
            await task.Result.DisposeAsync();
        await first.Received(1).DisposeAsync();
        await second.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Initialization_and_cleanup_failures_are_both_preserved()
    {
        var location = Location("alpha");
        var document = Document(location);
        var initial = new InvalidOperationException("synthetic initialization failure");
        var cleanup = new IOException("synthetic cleanup failure");
        document.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(initial));
        document.DisposeAsync().Returns(ValueTask.FromException(cleanup));

        var observed = await Should.ThrowAsync<AggregateException>(() =>
            PairFactory(Factory(document)).CreateAsync(location).AsTask());

        observed.InnerExceptions.ShouldBe(new Exception[] { initial, cleanup });
        await document.Received(1).DisposeAsync();
    }

    private static AeroDBTenantStoreLocation Location(string tenant) =>
        new(tenant, "primary", "ca_" + tenant, "main");

    private static IDocumentStore Document(AeroDBTenantStoreLocation location, string endpoint = "http://localhost:8000")
    {
        var document = Substitute.For<IDocumentStore>();
        document.Options.Returns(new StoreOptions
        {
            Endpoint = endpoint, Namespace = location.Namespace, Database = location.Database,
            Username = "credentials-canary", Password = "password-canary"
        });
        document.Client.Returns(Substitute.For<ISurrealDbClient, IAsyncDisposable>());
        document.InitializeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return document;
    }

    private static IAeroDBTenantDocumentStoreFactory Factory(IDocumentStore document)
    {
        var documents = Substitute.For<IAeroDBTenantDocumentStoreFactory>();
        documents.CreateAsync(Arg.Any<AeroDBTenantStoreLocation>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(document));
        return documents;
    }

    private static AeroDBTenantStorePairFactory PairFactory(IAeroDBTenantDocumentStoreFactory documents) =>
        new(documents, NullLogger<AeroDBMessageStore>.Instance);
}
