using System.Reflection;
using Dali;

namespace WolverineFx.Dali;

/// <summary>
/// Dali <see cref="IDocumentSessionListener"/> that publishes events appended
/// during a Wolverine-enrolled session as Wolverine messages through the outbox.
/// Events are forwarded in <see cref="BeforeSaveChangesAsync"/> so they are
/// enlisted in Wolverine's outbox and flushed atomically with the transaction.
///
/// The listener is registered once on <see cref="StoreOptions.Listeners"/> and
/// uses an <see cref="AsyncLocal{T}"/> to receive the scoped
/// <see cref="Wolverine.IMessageContext"/> for each handler invocation
/// (set by <see cref="DaliOutboxedSessionFactory"/>).
/// </summary>
internal sealed class DaliEventForwarding : IDocumentSessionListener
{
    /// <summary>
    /// Async-local storage for the current Wolverine message context.
    /// Flows with the async execution context through SaveChangesAsync.
    /// </summary>
    internal static readonly AsyncLocal<Wolverine.IMessageContext?> CurrentContext = new();

    /// <summary>
    /// Sets the current Wolverine message context for event forwarding.
    /// Called by <see cref="DaliOutboxedSessionFactory"/> when opening a session.
    /// </summary>
    internal static void SetCurrentContext(Wolverine.IMessageContext? context)
    {
        CurrentContext.Value = context;
    }

    // Reflection caches for accessing internal DocumentSession._appendedEvents
    private static readonly FieldInfo AppendedEventsField =
        typeof(DocumentSession).GetField("_appendedEvents", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "DocumentSession._appendedEvents field not found. Dali version mismatch.");

    private static readonly FieldInfo ValueTupleEventField =
        typeof(ValueTuple<string, object>).GetField("Item2")
        ?? throw new InvalidOperationException(
            "ValueTuple<string, object>.Item2 field not found.");

    /// <summary>
    /// Called before SaveChangesAsync persists entities. Publishes each
    /// appended event as a Wolverine message through the outbox context.
    /// </summary>
    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        var context = CurrentContext.Value;
        if (context is null) return;

        // Read _appendedEvents via reflection (internal field on DocumentSession)
        var list = AppendedEventsField.GetValue(session) as System.Collections.IList;
        if (list is null || list.Count == 0) return;

        // Extract the event object from each (string StreamId, object Event) tuple
        var events = new List<object>(list.Count);
        foreach (var item in list)
        {
            if (ValueTupleEventField.GetValue(item) is object evt)
            {
                events.Add(evt);
            }
        }

        // Publish each event through the message context.
        // This enlists the message in Wolverine's outbox, which will be
        // flushed atomically after the transaction commits.
        foreach (var evt in events)
        {
            await context.PublishAsync(evt);
        }
    }
}
