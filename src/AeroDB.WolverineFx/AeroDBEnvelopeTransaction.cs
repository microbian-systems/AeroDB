using AeroDB.Sable;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace AeroDB.WolverineFx;

/// <summary>
/// Wraps AeroDBMessageStore and an IDocumentSession to provide Wolverine's
/// IEnvelopeTransaction contract. Envelope operations are queued and
/// committed within AeroDB.Sable's SurrealDB transaction lifecycle.
/// </summary>
internal sealed class AeroDBEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly AeroDBMessageStore _store;
    private readonly IDocumentSession _session;
    private readonly int _ownerId;
    private readonly List<Envelope> _pendingOutgoing = [];
    private readonly List<Envelope> _pendingIncoming = [];
    private readonly List<Envelope> _pendingHandledIncoming = [];

    public AeroDBEnvelopeTransaction(AeroDBMessageStore store, IDocumentSession session, int ownerId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownerId = ownerId;
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        var command = _store.BuildOutgoingCreateCommand(envelope, _ownerId);
        _session.QueueRecordMutation(WolverineRecordMutationFactory.CreateOutgoing(command));
        _pendingOutgoing.Add(envelope);
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (envelopes is null) throw new ArgumentNullException(nameof(envelopes));
        return Task.WhenAll(envelopes.Select(e => PersistOutgoingAsync(e)));
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        var command = _store.BuildIncomingCreateCommand(envelope, _ownerId);
        _session.QueueRecordMutation(WolverineRecordMutationFactory.CreateIncoming(command));
        _pendingIncoming.Add(envelope);
        return Task.CompletedTask;
    }

    public async ValueTask RollbackAsync()
    {
        try
        {
            if (_session is DocumentSession documentSession)
                await documentSession.RollbackTransactionIfActiveAsync().ConfigureAwait(false);

            _session.ClearChanges();
        }
        finally
        {
            _pendingOutgoing.Clear();
            _pendingIncoming.Clear();
            _pendingHandledIncoming.Clear();
        }
    }

    public Task<bool> TryMakeEagerIdempotencyCheckAsync(
        Envelope envelope,
        DurabilitySettings settings,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(settings);
        cancellation.ThrowIfCancellationRequested();
        return Task.FromException<bool>(new NotSupportedException(
            "AeroDB's transactional outbox cannot perform eager inbox idempotency checks. " +
            "Eager checks require a handler-spanning transaction and must be provided by Wolverine middleware."));
    }

    internal bool IsIncomingPersistencePending(Envelope envelope) =>
        _pendingIncoming.Any(candidate => candidate.Id == envelope.Id);

    internal bool IsIncomingHandlingPending(Envelope envelope) =>
        _pendingHandledIncoming.Any(candidate => candidate.Id == envelope.Id);

    internal void QueueHandlingForPreviouslyPersistedIncoming(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (IsIncomingHandlingPending(envelope)) return;

        var command = _store.BuildMarkIncomingHandledCommand(envelope, DateTimeOffset.UtcNow.AddDays(1));
        _session.QueueRecordMutation(WolverineRecordMutationFactory.MarkIncomingHandled(command));
        _pendingHandledIncoming.Add(envelope);
    }

    internal void QueueHandlingCompletedInCommit(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsIncomingHandlingPending(envelope))
            _pendingHandledIncoming.Add(envelope);
    }

    internal void MarkCommitted()
    {
        foreach (var envelope in _pendingOutgoing)
            envelope.WasPersistedInOutbox = true;

        foreach (var envelope in _pendingIncoming)
            envelope.WasPersistedInInbox = true;

        foreach (var envelope in _pendingHandledIncoming)
        {
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
        }

        _pendingOutgoing.Clear();
        _pendingIncoming.Clear();
        _pendingHandledIncoming.Clear();
    }
}
