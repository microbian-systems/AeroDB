using AeroDB.Sable;
using NSubstitute;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;
using SurrealDb.Net.Models.Auth;
using TUnit.Core;

namespace AeroDB.Tests;

[NotInParallel]
public sealed class DocumentStoreInitializationTests
{
    [Test]
    public async Task Authenticated_child_session_signs_in_before_selecting_database()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var child = Substitute.For<ISurrealDbSession>();
        client.CreateSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(child));
        child.SignIn(Arg.Any<RootAuth>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        child.Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var store = new DocumentStore(new StoreOptions
        {
            Username = "root-user",
            Password = "root-password"
        });

        await using var session = await store.CreateAuthenticatedSessionAsync(client, "app", "cms");

        await child.Received(1).SignIn(
            Arg.Is<RootAuth>(auth => auth.Username == "root-user" && auth.Password == "root-password"),
            Arg.Any<CancellationToken>());
        await child.Received(1).Use("app", "cms", Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            child.SignIn(Arg.Any<RootAuth>(), Arg.Any<CancellationToken>());
            child.Use("app", "cms", Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Authenticated_child_sessions_keep_their_requested_database_scope()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var firstChild = Substitute.For<ISurrealDbSession>();
        var secondChild = Substitute.For<ISurrealDbSession>();
        client.CreateSession(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(firstChild),
            Task.FromResult(secondChild));
        firstChild.SignIn(Arg.Any<RootAuth>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        secondChild.SignIn(Arg.Any<RootAuth>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        firstChild.Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        secondChild.Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var store = new DocumentStore(new StoreOptions
        {
            Username = "root-user",
            Password = "root-password"
        });
        await using var firstSession = await store.CreateAuthenticatedSessionAsync(client, "tenant-ns", "tenant-a");
        await using var secondSession = await store.CreateAuthenticatedSessionAsync(client, "tenant-ns", "tenant-b");

        firstSession.ShouldBeSameAs(firstChild);
        secondSession.ShouldBeSameAs(secondChild);
        await firstChild.Received(1).Use("tenant-ns", "tenant-a", Arg.Any<CancellationToken>());
        await firstChild.DidNotReceive().Use("tenant-ns", "tenant-b", Arg.Any<CancellationToken>());
        await secondChild.Received(1).Use("tenant-ns", "tenant-b", Arg.Any<CancellationToken>());
        await secondChild.DidNotReceive().Use("tenant-ns", "tenant-a", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Authenticated_parent_session_is_forked_before_selecting_database()
    {
        var substitute = Substitute.For(
            new[] { typeof(ISurrealDbClient), typeof(ISurrealDbSession) },
            Array.Empty<object>());
        var parent = (ISurrealDbSession)substitute;
        var client = (ISurrealDbClient)substitute;
        var child = Substitute.For<ISurrealDbSession>();
        parent.ForkSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(child));
        child.Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var store = new DocumentStore(new StoreOptions { Token = "configured-token" });

        await using var session = await store.CreateAuthenticatedSessionAsync(client, "app", "cms");

        await parent.Received(1).ForkSession(Arg.Any<CancellationToken>());
        await child.Received(1).Use("app", "cms", Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateSession(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Failed_child_authentication_disposes_the_isolated_session()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var child = Substitute.For<ISurrealDbSession>();
        client.CreateSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(child));
        child.SignIn(Arg.Any<RootAuth>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("sign-in failed")));
        child.DisposeAsync().Returns(ValueTask.CompletedTask);

        await using var store = new DocumentStore(new StoreOptions
        {
            Username = "root-user",
            Password = "root-password"
        });

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.CreateAuthenticatedSessionAsync(client, "app", "cms"));

        await child.Received(1).DisposeAsync();
        await child.DidNotReceive().Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Failed_child_database_selection_disposes_the_isolated_session()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var child = Substitute.For<ISurrealDbSession>();
        client.CreateSession(Arg.Any<CancellationToken>()).Returns(Task.FromResult(child));
        child.Use(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("use failed")));
        child.DisposeAsync().Returns(ValueTask.CompletedTask);

        await using var store = new DocumentStore(new StoreOptions());

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.CreateAuthenticatedSessionAsync(client, "app", "cms"));

        await child.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Token_only_child_session_creation_fails_closed_before_creating_an_anonymous_session()
    {
        var client = Substitute.For<ISurrealDbClient>();
        await using var store = new DocumentStore(new StoreOptions { Token = "configured-token" });

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => store.CreateAuthenticatedSessionAsync(client, "app", "cms"));

        exception.Message.ShouldContain("Token-authenticated child sessions");
        await client.DidNotReceive().CreateSession(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Advanced_public_session_factory_uses_the_configured_document_store_delegate()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var delegatedSession = Substitute.For<ISurrealDbSession>();
        delegatedSession.DisposeAsync().Returns(ValueTask.CompletedTask);
        var delegateCalls = 0;
        store.Options.Advanced.CreateSessionAsync = _ =>
        {
            delegateCalls++;
            return Task.FromResult(delegatedSession);
        };

        await using var session = await store.Advanced.CreateSessionAsync();

        session.ShouldBeSameAs(delegatedSession);
        delegateCalls.ShouldBe(1);
    }

    [Test]
    public Task Live_query_uses_configured_database_for_conjoined_tenancy()
    {
        var options = new StoreOptions
        {
            TenancyStyle = TenancyStyle.Conjoined,
            Namespace = "app",
            Database = "configured-db",
            DefaultTenantId = "tenant-a"
        };

        DocumentStore.GetLiveQueryDatabaseName(options, options.DefaultTenantId)
            .ShouldBe("configured-db");

        return Task.CompletedTask;
    }

    [Test]
    public Task Live_query_uses_tenant_database_for_database_per_tenant_tenancy()
    {
        var options = new StoreOptions
        {
            TenancyStyle = TenancyStyle.DatabasePerTenant,
            Namespace = "app",
            Database = "configured-db"
        };

        DocumentStore.GetLiveQueryDatabaseName(options, "tenant-a")
            .ShouldBe("app_tenant-a");

        return Task.CompletedTask;
    }

    [Test]
    public async Task Concurrent_initialize_call_waits_for_active_initialization()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var store = Documents.For(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.AsyncConfigurators.Add(new BlockingConfigurator(entered, release));
        });

        var firstInitialization = store.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var concurrentInitialization = store.InitializeAsync();

        try
        {
            concurrentInitialization.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(firstInitialization, concurrentInitialization);
    }

    [Test]
    public async Task Failed_initialization_releases_embedded_database_immediately()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"aerodb_failed_initialization_{Guid.NewGuid():N}");
        var store = Documents.For(options =>
        {
            options.ClientFactory = () => new SurrealDbKvClient(dataPath);
            options.Schema.For<FailureDocument>()
                .FullTextIndex(x => x.Name, "missing_failure_analyzer");
        });

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => store.InitializeAsync());

            await using var nextClient = new SurrealDbKvClient(dataPath);
            Func<Task> connectAgain = () => nextClient.Connect();

            await connectAgain.ShouldNotThrowAsync();
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, recursive: true);
        }
    }

    private sealed class BlockingConfigurator(
        TaskCompletionSource entered,
        TaskCompletionSource release) : IAsyncConfigureAeroDB
    {
        public async Task ConfigureAsync(StoreOptions options, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
    }

    private sealed class FailureDocument
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
