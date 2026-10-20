using System.Reflection;
using AeroDB.Sable;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Runtime;

namespace AeroDB.Tests;

public class FlushOutgoingMessagesOnAeroDBCommitTests
{
    private static readonly PropertyInfo EnvelopeProperty =
        typeof(MessageBus).GetProperty("Envelope", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    private static Envelope MakeEnvelope() => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "Test",
        Data = [1, 2, 3]
    };

    private static (MessageContext Context, AeroDBMessageStore Store) CreateContextWithStore(ISurrealDbClient client)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        var store = new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);
        runtime.Storage.Returns(store);
        return (new MessageContext(runtime), store);
    }

    private static void SetEnvelope(MessageContext context, Envelope? envelope) =>
        EnvelopeProperty.SetValue(context, envelope);

    private static bool IsHandledMutation(ValidatedRecordMutation mutation, Envelope envelope) =>
        mutation.Kind == ValidatedRecordMutationKind.MergeExisting
        && mutation.Target.Table == "wolverine_incoming_envelopes"
        && mutation.Target.TryDeserializeId<string>(out var id)
        && id == envelope.Id.ToString("D")
        && mutation.Content.TryGetValue("status", out var status)
        && Equals(status, "Handled")
        && mutation.Content.TryGetValue("keep_until", out var keepUntil)
        && keepUntil is DateTimeOffset;

    [Test]
    public async Task BeforeCommitAsync_queues_a_typed_handled_mutation_for_a_newly_persisted_incoming_envelope()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var (context, store) = CreateContextWithStore(client);
        var envelope = MakeEnvelope();
        SetEnvelope(context, envelope);
        var transaction = new AeroDBEnvelopeTransaction(store, session, 0);
        await transaction.PersistIncomingAsync(envelope);
        session.ClearReceivedCalls();
        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, store);
        listener.AttachTransaction(transaction);

        await listener.BeforeCommitAsync(session, CancellationToken.None);

        session.Received(1).QueueRecordMutation(
            Arg.Is<ValidatedRecordMutation>(mutation => IsHandledMutation(mutation, envelope)));
        await session.DidNotReceiveWithAnyArgs().ExecuteSqlAsync(default!, default!, default);
        envelope.Status.ShouldNotBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task AfterCommitAsync_marks_pending_envelopes_committed_before_transport_flush()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var (context, store) = CreateContextWithStore(client);
        var outgoing = MakeEnvelope();
        var transaction = new AeroDBEnvelopeTransaction(store, session, 0);
        await transaction.PersistOutgoingAsync(outgoing);
        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, store);
        listener.AttachTransaction(transaction);

        await listener.AfterCommitAsync(session, Substitute.For<IChangeSet>(), CancellationToken.None);

        outgoing.WasPersistedInOutbox.ShouldBeTrue();
    }

    [Test]
    public async Task BeforeCommitAsync_queues_the_incoming_acknowledgement_only_once()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var (context, store) = CreateContextWithStore(client);
        var envelope = MakeEnvelope();
        SetEnvelope(context, envelope);
        var transaction = new AeroDBEnvelopeTransaction(store, session, 0);
        await transaction.PersistIncomingAsync(envelope);
        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, store);
        listener.AttachTransaction(transaction);

        await listener.BeforeCommitAsync(session, CancellationToken.None);
        await listener.BeforeCommitAsync(session, CancellationToken.None);

        session.Received(1).QueueRecordMutation(
            Arg.Is<ValidatedRecordMutation>(mutation => IsHandledMutation(mutation, envelope)));
        envelope.Status.ShouldNotBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task BeforeCommitAsync_honors_cancellation_without_marking_the_envelope_handled()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var session = Substitute.For<IDocumentSession>();
        var (context, store) = CreateContextWithStore(client);
        var envelope = MakeEnvelope();
        SetEnvelope(context, envelope);
        var transaction = new AeroDBEnvelopeTransaction(store, session, 0);
        await transaction.PersistIncomingAsync(envelope);
        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, store);
        listener.AttachTransaction(transaction);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        session.ClearReceivedCalls();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            listener.BeforeCommitAsync(session, cancellation.Token));

        session.DidNotReceiveWithAnyArgs().QueueRecordMutation(default!);
        envelope.Status.ShouldNotBe(EnvelopeStatus.Handled);
    }
}
