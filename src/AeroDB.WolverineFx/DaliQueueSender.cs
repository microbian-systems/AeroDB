using Wolverine;
using Wolverine.Transports.Sending;

namespace AeroDB.WolverineFx;

/// <summary>
/// Sends outgoing messages by inserting them into the SurrealDB
/// wolverine_outgoing_envelopes table via DaliMessageStore.
/// </summary>
public sealed class DaliQueueSender : ISender
{
    private readonly DaliMessageStore _store;

    /// <summary>
    /// Create a new sender with the given store and destination.
    /// </summary>
    public DaliQueueSender(DaliMessageStore store, Uri destination)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    /// <summary>
    /// This transport does not support native scheduled send.
    /// </summary>
    public bool SupportsNativeScheduledSend => false;

    /// <summary>
    /// The destination URI for this sender.
    /// </summary>
    public Uri Destination { get; }

    /// <summary>
    /// Ping to check if the store is reachable.
    /// </summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            await _store.CheckConnectivityAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Send an envelope by storing it in the outgoing table.
    /// </summary>
    public async ValueTask SendAsync(Envelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        // Ensure serialized data is available — access the Data getter which
        // triggers serialization if needed
        var _ = envelope.Data;

        await _store.Outbox.StoreOutgoingAsync(envelope, envelope.OwnerId);
    }
}
