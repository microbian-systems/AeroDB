using AeroDB.Sable;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Factory that creates <see cref="IDocumentSession"/> instances pre-enrolled in
/// Wolverine's outbox. Mirrors the Wolverine.Marten <c>OutboxedSessionFactory</c> pattern.
/// </summary>
public sealed class AeroDBOutboxedSessionFactory
{
    private readonly IDocumentStore _store;
    private readonly AeroDBMessageStore _messageStore;
    private readonly ILogger<AeroDBOutboxedSessionFactory> _logger;

    public AeroDBOutboxedSessionFactory(
        IDocumentStore store,
        AeroDBMessageStore messageStore,
        ILogger<AeroDBOutboxedSessionFactory> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The backing message store.</summary>
    internal AeroDBMessageStore MessageStore => _messageStore;

    /// <summary>
    /// Open a lightweight document session enrolled in the active Wolverine
    /// message context's outbox. If the message context has a tenant ID,
    /// the session is scoped to that tenant.
    /// </summary>
    public async Task<IDocumentSession> OpenSession(MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IDocumentStore store = _store;

        // If the message context carries a tenant ID, scope the session
        if (!string.IsNullOrEmpty(context.TenantId))
        {
            store = _store.WithTenant(context.TenantId);
        }

        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, _messageStore);
        var session = await store.OpenSessionAsync(CreateSessionOptions(listener));
        ConfigureSession(context, session, listener, _messageStore);
        return session;
    }

    /// <summary>
    /// Open a lightweight document session with tenant isolation, enrolled
    /// in the active Wolverine message context's outbox.
    /// </summary>
    public async Task<IDocumentSession> OpenSession(MessageContext context, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contextTenantId = context.TenantId;
        if (!string.IsNullOrEmpty(contextTenantId) &&
            !string.IsNullOrEmpty(tenantId) &&
            !string.Equals(contextTenantId, tenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested tenant does not match the message context tenant.");

        var effectiveTenantId = string.IsNullOrEmpty(contextTenantId) ? tenantId : contextTenantId;
        if (string.IsNullOrEmpty(contextTenantId) && !string.IsNullOrEmpty(tenantId))
            context.TenantId = tenantId;

        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, _messageStore);
        IDocumentSession session;
        if (!string.IsNullOrEmpty(effectiveTenantId))
        {
            var store = _store.WithTenant(effectiveTenantId);
            session = await store.OpenSessionAsync(CreateSessionOptions(listener));
        }
        else
        {
            session = await _store.OpenSessionAsync(CreateSessionOptions(listener));
        }

        ConfigureSession(context, session, listener, _messageStore);
        return session;
    }

    internal static SessionOptions CreateSessionOptions(FlushOutgoingMessagesOnAeroDBCommit listener) =>
        new()
        {
            Tracking = DocumentTracking.None,
            Listeners = [listener]
        };

    internal static void ConfigureSession(
        MessageContext context,
        IDocumentSession session,
        FlushOutgoingMessagesOnAeroDBCommit listener,
        AeroDBMessageStore messageStore)
    {
        var ownerId = messageStore.GetOwnerId();
        var tx = new AeroDBEnvelopeTransaction(messageStore, session, ownerId);
        listener.AttachTransaction(tx);

        // Incoming envelopes were durably persisted before their handler ran. Queue the
        // handled update now so a handler with no business writes still enters Sable's
        // SaveChanges lifecycle and receives post-commit delivery. This must happen
        // before changing MessageContext so a failed queue leaves the caller's
        // previous storage and transaction intact.
        if (context.Envelope?.WasPersistedInInbox == true)
            tx.QueueHandlingForPreviouslyPersistedIncoming(context.Envelope);

        context.OverrideStorage(messageStore);
        context.EnlistInOutbox(tx);
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

/// <summary>
/// Dormant factory for a message that has already been assigned to a validated
/// tenant. It deliberately has no DI registration: activating multi-tenant
/// Wolverine recovery is a separate, evidence-gated change.
/// </summary>
internal sealed class AeroDBTenantedOutboxedSessionFactory : IAeroDBTenantedOutboxedSessionFactory
{
    private readonly AeroDBTenantStorePairCache _pairs;

    public AeroDBTenantedOutboxedSessionFactory(AeroDBTenantStorePairCache pairs)
    {
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
    }

    public async Task<IDocumentSession> OpenSessionAsync(
        MessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var tenantId = context.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new JasperFx.MultiTenancy.UnknownTenantIdException(tenantId ?? string.Empty);

        var pair = await _pairs.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        EnsureTenantHasNotChanged(context, tenantId);

        IDocumentSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, pair.MessageStore);
            session = await pair.DocumentStore
                .OpenSessionAsync(AeroDBOutboxedSessionFactory.CreateSessionOptions(listener), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureTenantHasNotChanged(context, tenantId);
            AeroDBOutboxedSessionFactory.ConfigureSession(context, session, listener, pair.MessageStore);
            return session;
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    private static void EnsureTenantHasNotChanged(MessageContext context, string expectedTenantId)
    {
        if (!string.Equals(context.TenantId, expectedTenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("The message context tenant changed while its fixed tenant store was resolving.");
    }
}
