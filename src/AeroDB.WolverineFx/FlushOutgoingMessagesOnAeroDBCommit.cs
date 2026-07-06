using AeroDB;
using Wolverine;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB <see cref="IDocumentSessionListener"/> that integrates with Wolverine's outbox
/// and forwards appended events as Wolverine messages.
/// <list type="bullet">
///   <item><description><see cref="BeforeSaveChangesAsync"/> — captures appended events and publishes them through Wolverine's outbox.</description></item>
///   <item><description><see cref="BeforeCommitAsync"/> — marks the incoming envelope as handled within the SurrealDB transaction.</description></item>
///   <item><description><see cref="AfterCommitAsync"/> — flushes outgoing messages after the transaction commits successfully.</description></item>
/// </list>
///
/// Reflection access to <c>DocumentSession._appendedEvents</c> is
/// delegated to <see cref="Internal.AeroDBSessionEventAccessor"/> to avoid
/// duplicating that logic with <see cref="AeroDBEventForwarding"/>.
/// </summary>
internal sealed class FlushOutgoingMessagesOnAeroDBCommit : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private readonly AeroDBMessageStore _messageStore;

    public FlushOutgoingMessagesOnAeroDBCommit(MessageContext context, AeroDBMessageStore messageStore)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
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
    public async Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
    {
        if (_context.Envelope == null) return;

        if (_context.Envelope.WasPersistedInInbox)
        {
            await _messageStore.Inbox.MarkIncomingEnvelopeAsHandledAsync(_context.Envelope);
            _context.Envelope.Status = EnvelopeStatus.Handled;
        }
    }

    /// <summary>
    /// Called after the SurrealDB transaction has committed successfully.
    /// </summary>
    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        await _context.FlushOutgoingMessagesAsync();
    }

    public Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task BeforeDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
    public Task AfterDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
}
