namespace Dali;

/// <summary>
/// Event subscription for forwarding events to external handlers.
/// Implement this interface to receive and process events as they are appended
/// to streams. Subscriptions are managed by the async daemon and are tracked
/// by <see cref="SubscriptionName"/> for progress checkpointing.
/// <br/>
/// This is a simplified Marten-parity interface. For advanced subscriptions
/// with JasperFx types (<c>EventRange</c>,
/// <c>ISubscriptionController</c>),
/// use <see cref="IDaliSubscription"/> instead.
/// </summary>
public interface ISubscription
{
    /// <summary>
    /// Called when new events are appended to a stream. The implementation
    /// may use <paramref name="operations"/> for read/write access to the
    /// document store within the same transactional boundary.
    /// </summary>
    /// <param name="events">The batch of new events to process.</param>
    /// <param name="operations">Document operations for reading/writing within the session.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessEventsAsync(
        IReadOnlyList<IEvent> events,
        IDocumentOperations operations,
        CancellationToken ct);

    /// <summary>
    /// Called when the subscription is first started (e.g., daemon initialization).
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Called when the subscription is being shut down (e.g., daemon disposal).
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Filter: only events of these types trigger this subscription.
    /// When empty or null, all event types are processed.
    /// </summary>
    Type[] EventTypes { get; }

    /// <summary>
    /// Subscription name for diagnostics and progress tracking.
    /// Must be unique within the store.
    /// </summary>
    string SubscriptionName { get; }
}
