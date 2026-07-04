using AeroDB;

namespace AeroDB.Tests;

using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

/// <summary>
/// Mock-based unit tests for <see cref="AeroDBOutboxedSessionFactory"/>.
/// AeroDBMessageStore is sealed — tests create real instances with a mocked
/// ISurrealDbClient. Verifies session opening, tenant scoping, and outbox enlistment.
/// </summary>
public class AeroDBOutboxedSessionFactoryTests
{
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

        var session = await factory.OpenSession(context, "tenant-beta");

        session.ShouldBe(mockSession);
        documentStore.Received(1).WithTenant("tenant-beta");
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
    public async Task OpenSession_with_explicit_tenant_overrides_context_tenant()
    {
        var factory = CreateFactory(out var documentStore, out _);
        var tenantStore = Substitute.For<IDocumentStore>();
        var mockSession = Substitute.For<IDocumentSession>();
        documentStore.WithTenant("explicit-tenant").Returns(tenantStore);
        tenantStore.OpenSessionAsync(Arg.Any<SessionOptions>(), Arg.Any<CancellationToken>())
            .Returns(mockSession);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var context = new MessageContext(runtime);
        context.TenantId = "context-tenant"; // Should be overridden

        var session = await factory.OpenSession(context, "explicit-tenant");

        session.ShouldBe(mockSession);
        documentStore.Received(1).WithTenant("explicit-tenant");
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
