using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace AeroDB.Tests;

public class AeroDBEnvelopeTransactionTests
{
    private static AeroDBMessageStore CreateStore(ISurrealDbClient client) =>
        new(client, NullLogger<AeroDBMessageStore>.Instance);

    private static Envelope MakeEnvelope() => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "TestMessage",
        Data = [1, 2, 3]
    };

    private static bool IsExpectedCreateMutation(
        ValidatedRecordMutation mutation,
        string table,
        Envelope envelope)
    {
        if (mutation.Kind != ValidatedRecordMutationKind.Create
            || mutation.Target.Table != table
            || !mutation.Target.TryDeserializeId<string>(out var id)
            || id != envelope.Id.ToString("D"))
            return false;

        return mutation.Content.TryGetValue("body", out var body)
            && body is string base64
            && Convert.FromBase64String(base64).SequenceEqual(envelope.Data!);
    }

    [Test]
    public async Task PersistOutgoingAsync_queues_a_typed_create_on_the_document_session()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var transaction = new AeroDBEnvelopeTransaction(CreateStore(client), session, 42);
        var envelope = MakeEnvelope();

        await transaction.PersistOutgoingAsync(envelope);

        session.Received(1).QueueRecordMutation(
            Arg.Is<ValidatedRecordMutation>(mutation =>
                IsExpectedCreateMutation(mutation, "wolverine_outgoing_envelopes", envelope)));
        session.DidNotReceiveWithAnyArgs().QueueSqlCommand(default!, default!, default!);
        await client.DidNotReceiveWithAnyArgs().RawQuery(default!);
        envelope.WasPersistedInOutbox.ShouldBeFalse();
    }

    [Test]
    public async Task PersistIncomingAsync_queues_a_typed_create_on_the_document_session()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var transaction = new AeroDBEnvelopeTransaction(CreateStore(client), session, 42);
        var envelope = MakeEnvelope();

        await transaction.PersistIncomingAsync(envelope);

        session.Received(1).QueueRecordMutation(
            Arg.Is<ValidatedRecordMutation>(mutation =>
                IsExpectedCreateMutation(mutation, "wolverine_incoming_envelopes", envelope)));
        session.DidNotReceiveWithAnyArgs().QueueSqlCommand(default!, default!, default!);
        await client.DidNotReceiveWithAnyArgs().RawQuery(default!);
        envelope.WasPersistedInInbox.ShouldBeFalse();
    }

    [Test]
    public async Task RollbackAsync_clears_the_document_session_and_pending_envelopes()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var transaction = new AeroDBEnvelopeTransaction(CreateStore(client), session, 42);
        var envelope = MakeEnvelope();
        await transaction.PersistOutgoingAsync(envelope);

        await transaction.RollbackAsync();

        session.Received(1).ClearChanges();
        transaction.MarkCommitted();
        envelope.WasPersistedInOutbox.ShouldBeFalse();
    }

    [Test]
    public async Task TryMakeEagerIdempotencyCheckAsync_fails_fast_until_handler_spanning_transactions_exist()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var transaction = new AeroDBEnvelopeTransaction(CreateStore(client), session, 42);

        var exception = await Should.ThrowAsync<NotSupportedException>(() =>
            transaction.TryMakeEagerIdempotencyCheckAsync(
                MakeEnvelope(), new DurabilitySettings(), CancellationToken.None));

        exception.Message.ShouldContain("handler-spanning transaction");
        await client.DidNotReceiveWithAnyArgs().RawQuery(default!);
    }

    [Test]
    public async Task TryMakeEagerIdempotencyCheckAsync_honors_cancellation_before_reporting_unsupported_mode()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var transaction = new AeroDBEnvelopeTransaction(CreateStore(client), session, 42);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            transaction.TryMakeEagerIdempotencyCheckAsync(
                MakeEnvelope(), new DurabilitySettings(), cancellation.Token));
    }
}
