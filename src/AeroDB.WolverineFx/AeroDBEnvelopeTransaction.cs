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

    public AeroDBEnvelopeTransaction(AeroDBMessageStore store, IDocumentSession session, int ownerId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownerId = ownerId;
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        return _store.Outbox.StoreOutgoingAsync(envelope, _ownerId);
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        if (envelopes is null) throw new ArgumentNullException(nameof(envelopes));
        return Task.WhenAll(envelopes.Select(e => PersistOutgoingAsync(e)));
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        return _store.Inbox.StoreIncomingAsync(envelope);
    }

    public ValueTask RollbackAsync()
    {
        // Transaction rollback is handled by AeroDB.Sable's SaveChangesAsync Cancel path.
        // No additional action needed here.
        return ValueTask.CompletedTask;
    }

    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(
        Envelope envelope,
        DurabilitySettings settings,
        CancellationToken cancellation)
    {
        if (envelope.WasPersistedInInbox) return true;

        try
        {
            var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
            await PersistIncomingAsync(copy);
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
