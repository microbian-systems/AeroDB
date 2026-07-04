using AeroDB;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Factory that creates <see cref="IDocumentSession"/> instances pre-enrolled in
/// Wolverine's outbox. Mirrors the Wolverine.Marten <c>OutboxedSessionFactory</c> pattern.
/// </summary>
public sealed class DaliOutboxedSessionFactory
{
    private readonly IDocumentStore _store;
    private readonly DaliMessageStore _messageStore;
    private readonly ILogger<DaliOutboxedSessionFactory> _logger;

    public DaliOutboxedSessionFactory(
        IDocumentStore store,
        DaliMessageStore messageStore,
        ILogger<DaliOutboxedSessionFactory> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The backing message store.</summary>
    internal DaliMessageStore MessageStore => _messageStore;

    /// <summary>
    /// Open a lightweight document session enrolled in the active Wolverine
    /// message context's outbox. If the message context has a tenant ID,
    /// the session is scoped to that tenant.
    /// </summary>
    public async Task<IDocumentSession> OpenSession(MessageContext context)
    {
        IDocumentStore store = _store;

        // If the message context carries a tenant ID, scope the session
        if (!string.IsNullOrEmpty(context.TenantId))
        {
            store = _store.WithTenant(context.TenantId);
        }

        var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        ConfigureSession(context, session);
        return session;
    }

    /// <summary>
    /// Open a lightweight document session with tenant isolation, enrolled
    /// in the active Wolverine message context's outbox.
    /// </summary>
    public async Task<IDocumentSession> OpenSession(MessageContext context, string? tenantId)
    {
        context.TenantId ??= tenantId;

        IDocumentSession session;
        if (!string.IsNullOrEmpty(tenantId))
        {
            var store = _store.WithTenant(tenantId);
            session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        }
        else
        {
            session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        }

        ConfigureSession(context, session);
        return session;
    }

    private static bool _forwarderRegistered;

    private void ConfigureSession(MessageContext context, IDocumentSession session)
    {
        context.OverrideStorage(_messageStore);

        // Enlist the transaction in Wolverine's outbox
        var ownerId = _messageStore.GetOwnerId();
        var tx = new DaliEnvelopeTransaction(_messageStore, session, ownerId);
        context.EnlistInOutbox(tx);

        // One-time registration of the event forwarder on the store's global listener list.
        // Access the store options' Listeners list (shared across all sessions).
        if (!_forwarderRegistered)
        {
            if (!_store.Options.Listeners.OfType<DaliEventForwarding>().Any())
            {
                _store.Options.Listeners.Add(new DaliEventForwarding());
            }
            _forwarderRegistered = true;
        }

        // Wire the scoped message context so DaliEventForwarding can publish events
        DaliEventForwarding.SetCurrentContext(context);
    }

    /// <summary>
    /// Open an outbox-enrolled session from an <see cref="IMessageBus"/> reference.
    /// </summary>
    public async Task<IDocumentSession> OpenSession(IMessageBus bus)
    {
        if (bus is MessageContext context)
            return await OpenSession(context);

        throw new ArgumentException("The provided bus is not a MessageContext.", nameof(bus));
    }
}
