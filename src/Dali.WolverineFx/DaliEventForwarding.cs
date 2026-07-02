using Dali;

namespace Dali.WolverineFx;

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
///
/// Reflection access to <c>DocumentSession._appendedEvents</c> is
/// delegated to <see cref="Internal.DaliSessionEventAccessor"/> to avoid
/// duplicating that logic with <see cref="FlushOutgoingMessagesOnDaliCommit"/>.
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

    /// <summary>
    /// Called before SaveChangesAsync persists entities. Publishes each
    /// appended event as a Wolverine message through the outbox context.
    /// </summary>
    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        var context = CurrentContext.Value;
        if (context is null) return;

        if (session is not DocumentSession daliSession) return;

        var events = Internal.DaliSessionEventAccessor.GetAppendedEvents(daliSession);
        if (events.Count == 0) return;

        // Publish each event through the message context.
        // This enlists the message in Wolverine's outbox, which will be
        // flushed atomically after the transaction commits.
        foreach (var evt in events)
        {
            await context.PublishAsync(evt);
        }
    }
}
