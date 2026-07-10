using AeroDB.WolverineFx;

namespace AeroDB.Tests;
using System.Reflection;
using Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

public class FlushOutgoingMessagesOnAeroDBCommitTests
{
    private static readonly PropertyInfo? _envelopeProp =
        typeof(MessageBus).GetProperty("Envelope",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static (IWolverineRuntime Runtime, AeroDBMessageStore Store) CreateRuntimeWithStore(
        ISurrealDbClient? client = null)
    {
        client ??= Substitute.For<ISurrealDbClient>();
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Storage.Returns(store);
        runtime.Options.Returns(new WolverineOptions());
        return (runtime, store);
    }

    private static MessageContext CreateContext(IWolverineRuntime runtime)
    {
        return new MessageContext(runtime);
    }

    private static void SetEnvelope(MessageContext context, Envelope? envelope)
    {
        _envelopeProp!.SetValue(context, envelope);
    }

    [Test]
    public async Task BeforeCommitAsync_MarksHandled_WhenEnvelopeWasPersistedInInbox()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var (runtime, store) = CreateRuntimeWithStore(client);
        var context = CreateContext(runtime);
        var session = Substitute.For<IDocumentSession>();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "Test",
            Destination = new Uri("aerodb://localhost/queue")
        };
        envelope.Data = new byte[] { 1, 2, 3 };
        envelope.WasPersistedInInbox = true;
        SetEnvelope(context, envelope);

        var middleware = new FlushOutgoingMessagesOnAeroDBCommit(context, store);

        await middleware.BeforeCommitAsync(session, CancellationToken.None);

        envelope.Status.ShouldBe(EnvelopeStatus.Handled);
        await client.Received(1).RawQuery(Arg.Any<string>());
    }

    [Test]
    public async Task BeforeCommitAsync_MarksHandled_WhenDestinationIsNull()
    {
        // KEY TEST: fixes the bug where Destination != null guard prevented marking
        var client = Substitute.For<ISurrealDbClient>();
        var (runtime, store) = CreateRuntimeWithStore(client);
        var context = CreateContext(runtime);
        var session = Substitute.For<IDocumentSession>();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "LocalMessage",
            Destination = null
        };
        envelope.Data = new byte[] { 1, 2, 3 };
        envelope.WasPersistedInInbox = true;
        SetEnvelope(context, envelope);

        var middleware = new FlushOutgoingMessagesOnAeroDBCommit(context, store);

        await middleware.BeforeCommitAsync(session, CancellationToken.None);

        envelope.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task BeforeCommitAsync_Skips_WhenEnvelopeNull()
    {
        var (runtime, store) = CreateRuntimeWithStore();
        var context = CreateContext(runtime);
        var session = Substitute.For<IDocumentSession>();

        SetEnvelope(context, null);

        var middleware = new FlushOutgoingMessagesOnAeroDBCommit(context, store);

        await middleware.BeforeCommitAsync(session, CancellationToken.None);
    }

    [Test]
    public async Task BeforeCommitAsync_Skips_WhenNotPersistedInInbox()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var (runtime, store) = CreateRuntimeWithStore(client);
        var context = CreateContext(runtime);
        var session = Substitute.For<IDocumentSession>();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "NotPersisted",
            Destination = new Uri("aerodb://localhost/queue")
        };
        envelope.Data = new byte[] { 1, 2, 3 };
        envelope.WasPersistedInInbox = false;
        SetEnvelope(context, envelope);

        var middleware = new FlushOutgoingMessagesOnAeroDBCommit(context, store);

        await middleware.BeforeCommitAsync(session, CancellationToken.None);

        envelope.Status.ShouldNotBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task AfterCommitAsync_FlushesOutgoingMessages()
    {
        var (runtime, store) = CreateRuntimeWithStore();
        var context = CreateContext(runtime);
        var session = Substitute.For<IDocumentSession>();
        var changes = Substitute.For<IChangeSet>();

        var middleware = new FlushOutgoingMessagesOnAeroDBCommit(context, store);

        // Should not throw — FlushOutgoingMessagesAsync is exercised by integration tests
        await middleware.AfterCommitAsync(session, changes, CancellationToken.None);
    }
}
