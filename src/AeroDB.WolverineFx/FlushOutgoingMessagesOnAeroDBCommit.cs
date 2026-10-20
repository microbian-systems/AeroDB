using AeroDB.Sable;
using Wolverine;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB.Sable <see cref="IDocumentSessionListener"/> that integrates with Wolverine's outbox
/// and forwards appended events as Wolverine messages.
/// <list type="bullet">
///   <item><description><see cref="BeforeSaveChangesAsync"/> — captures appended events and publishes them through Wolverine's outbox.</description></item>
///   <item><description><see cref="BeforeCommitAsync"/> — marks the incoming envelope as handled within the SurrealDB transaction.</description></item>
///   <item><description><see cref="AfterCommitAsync"/> — flushes outgoing messages after the transaction commits successfully.</description></item>
/// </list>
///
/// Reflection access to <c>DocumentSession._appendedEvents</c> is
/// delegated to <see cref="Internal.AeroDBSessionEventAccessor"/> so the
/// outbox listener owns a single event-publication path for its session.
/// </summary>
internal sealed class FlushOutgoingMessagesOnAeroDBCommit : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private readonly AeroDBMessageStore _messageStore;
    private AeroDBEnvelopeTransaction? _transaction;

    public FlushOutgoingMessagesOnAeroDBCommit(MessageContext context, AeroDBMessageStore messageStore)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
    }

    internal void AttachTransaction(AeroDBEnvelopeTransaction transaction)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    /// <summary>
    /// Called before SaveChangesAsync persists entities. Publishes each
    /// appended event as a Wolverine message through the outbox context.
    /// </summary>
    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        if (session is not DocumentSession AeroDBSession) return;

        var events = Internal.AeroDBSessionEventAccessor.GetAppendedEvents(AeroDBSession);
        if (events.Count == 0) return;

        foreach (var evt in events)
        {
            await _context.PublishAsync(evt);
        }
    }

    public Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called just before the SurrealDB transaction commits.
    /// </summary>
    public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Envelope is not { } envelope || _transaction is null) return Task.CompletedTask;

        if (_transaction.IsIncomingPersistencePending(envelope)
            && !_transaction.IsIncomingHandlingPending(envelope))
        {
            ct.ThrowIfCancellationRequested();
            var command = _messageStore.BuildMarkIncomingHandledCommand(
                envelope,
                DateTimeOffset.UtcNow.AddDays(1));
            session.QueueRecordMutation(WolverineRecordMutationFactory.MarkIncomingHandled(command));
            _transaction.QueueHandlingCompletedInCommit(envelope);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after the SurrealDB transaction has committed successfully.
    /// </summary>
    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        // The database commit is authoritative. Mark in-memory persistence before
        // flushing so a cancellation during transport delivery leaves the durable
        // outbox recoverable instead of misrepresenting the committed state.
        _transaction?.MarkCommitted();
        await _context.FlushOutgoingMessagesAsync();
    }

    public Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task BeforeDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task AfterDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
}
