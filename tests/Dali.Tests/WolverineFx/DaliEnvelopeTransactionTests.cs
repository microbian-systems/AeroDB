namespace Dali.Tests;

using Dali.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;

/// <summary>
/// Mock-based unit tests for <see cref="DaliEnvelopeTransaction"/>.
/// DaliMessageStore and SurrealDbResponse are sealed — tests create real
/// store instances with a mocked ISurrealDbClient and verify delegation
/// through client call patterns and interface contracts.
/// No Wolverine runtime or SurrealDB involved.
/// </summary>
public class DaliEnvelopeTransactionTests
{
    private static DaliMessageStore CreateStore(ISurrealDbClient client)
    {
        return new DaliMessageStore(client, NullLogger<DaliMessageStore>.Instance);
    }

    private static Envelope MakeEnvelope()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "TestMessage"
        };
        env.Data = [1, 2, 3];
        return env;
    }

    // ─── Constructor ─────────────────────────────────────────────────

    [Test]
    public async Task Constructor_throws_when_store_is_null()
    {
        var session = Substitute.For<IDocumentSession>();
        Should.Throw<ArgumentNullException>(() =>
            new DaliEnvelopeTransaction(null!, session, 0));
    }

    [Test]
    public async Task Constructor_throws_when_session_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        Should.Throw<ArgumentNullException>(() =>
            new DaliEnvelopeTransaction(store, null!, 0));
    }

    // ─── PersistOutgoingAsync (single) ─────────────────────────────

    [Test]
    public async Task PersistOutgoingAsync_delegates_to_StoreOutgoingAsync()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 42);
        var envelope = MakeEnvelope();

        await tx.PersistOutgoingAsync(envelope);

        // Verify a CREATE query was sent to the outgoing table
        await client.Received(1).RawQuery(Arg.Is<string>(s =>
            s.Contains("CREATE wolverine_outgoing_envelopes")));
    }

    [Test]
    public async Task PersistOutgoingAsync_throws_when_envelope_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            tx.PersistOutgoingAsync((Envelope)null!));
    }

    // ─── PersistOutgoingAsync (array) ──────────────────────────────

    [Test]
    public async Task PersistOutgoingAsync_array_throws_when_envelopes_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            tx.PersistOutgoingAsync((Envelope[])null!));
    }

    [Test]
    public async Task PersistOutgoingAsync_array_calls_StoreOutgoingAsync_for_each_envelope()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 42);
        var env1 = MakeEnvelope();
        var env2 = MakeEnvelope();
        var env3 = MakeEnvelope();

        await tx.PersistOutgoingAsync([env1, env2, env3]);

        // Three CREATE queries expected
        await client.Received(3).RawQuery(Arg.Is<string>(s =>
            s.Contains("CREATE wolverine_outgoing_envelopes")));
    }

    [Test]
    public async Task PersistOutgoingAsync_empty_array_does_not_call_store()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 42);

        await tx.PersistOutgoingAsync([]);

        await client.DidNotReceiveWithAnyArgs().RawQuery(Arg.Any<string>());
    }

    // ─── PersistIncomingAsync ──────────────────────────────────────

    [Test]
    public async Task PersistIncomingAsync_delegates_to_StoreIncomingAsync()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);
        var envelope = MakeEnvelope();

        await tx.PersistIncomingAsync(envelope);

        await client.Received(1).RawQuery(Arg.Is<string>(s =>
            s.Contains("CREATE wolverine_incoming_envelopes")));
    }

    [Test]
    public async Task PersistIncomingAsync_throws_when_envelope_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            tx.PersistIncomingAsync(null!));
    }

    // ─── RollbackAsync ─────────────────────────────────────────────

    [Test]
    public async Task RollbackAsync_is_no_op_does_not_throw()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);

        await tx.RollbackAsync().AsTask();
    }

    // ─── TryMakeEagerIdempotencyCheckAsync ─────────────────────────

    [Test]
    public async Task TryMakeEagerIdempotencyCheckAsync_returns_true_when_already_in_inbox()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);
        var envelope = MakeEnvelope();
        envelope.WasPersistedInInbox = true;
        var settings = new DurabilitySettings();

        var result = await tx.TryMakeEagerIdempotencyCheckAsync(envelope, settings, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task TryMakeEagerIdempotencyCheckAsync_persists_and_returns_true_on_success()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);
        var envelope = MakeEnvelope();
        envelope.WasPersistedInInbox = false;
        var settings = new DurabilitySettings();

        var result = await tx.TryMakeEagerIdempotencyCheckAsync(envelope, settings, CancellationToken.None);

        result.ShouldBeTrue();
        envelope.WasPersistedInInbox.ShouldBeTrue();
    }

    [Test]
    public async Task TryMakeEagerIdempotencyCheckAsync_returns_false_when_persist_fails()
    {
        var client = Substitute.For<ISurrealDbClient>();
        // Make the RawQuery throw to simulate DB failure
        client.RawQuery(Arg.Any<string>())
            .Returns(Task.FromException<SurrealDb.Net.Models.Response.SurrealDbResponse>(
                new InvalidOperationException("db failure")));

        var store = CreateStore(client);
        var session = Substitute.For<IDocumentSession>();
        var tx = new DaliEnvelopeTransaction(store, session, 0);
        var envelope = MakeEnvelope();
        envelope.WasPersistedInInbox = false;
        var settings = new DurabilitySettings();

        var result = await tx.TryMakeEagerIdempotencyCheckAsync(envelope, settings, CancellationToken.None);

        result.ShouldBeFalse();
    }
}
