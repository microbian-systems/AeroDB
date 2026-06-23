using System.Reflection;
using Dali;
using Wolverine;
using Wolverine.Runtime;

namespace WolverineFx.Dali;

/// <summary>
/// Dali <see cref="IDocumentSessionListener"/> that integrates with Wolverine's outbox
/// and forwards appended events as Wolverine messages.
/// <list type="bullet">
///   <item><description><see cref="BeforeSaveChangesAsync"/> — captures appended events and publishes them through Wolverine's outbox.</description></item>
///   <item><description><see cref="BeforeCommitAsync"/> — marks the incoming envelope as handled within the SurrealDB transaction.</description></item>
///   <item><description><see cref="AfterCommitAsync"/> — flushes outgoing messages after the transaction commits successfully.</description></item>
/// </list>
/// </summary>
internal sealed class FlushOutgoingMessagesOnDaliCommit : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private readonly DaliMessageStore _messageStore;

    // Reflection caches for accessing internal DocumentSession._appendedEvents
    private static readonly FieldInfo AppendedEventsField =
        typeof(DocumentSession).GetField("_appendedEvents", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "DocumentSession._appendedEvents field not found. Dali version mismatch.");

    private static readonly FieldInfo ValueTupleEventField =
        typeof(ValueTuple<string, object>).GetField("Item2")
        ?? throw new InvalidOperationException(
            "ValueTuple<string, object>.Item2 field not found.");

    public FlushOutgoingMessagesOnDaliCommit(MessageContext context, DaliMessageStore messageStore)
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
        // Capture appended events and publish them through the outbox
        var list = AppendedEventsField.GetValue(session) as System.Collections.IList;
        if (list is null || list.Count == 0) return;

        var events = new List<object>(list.Count);
        foreach (var item in list)
        {
            if (ValueTupleEventField.GetValue(item) is object evt)
            {
                events.Add(evt);
            }
        }

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

        if (_context.Envelope.Destination != null && _context.Envelope.WasPersistedInInbox)
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
