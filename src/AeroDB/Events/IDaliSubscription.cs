using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace AeroDB;

/// <summary>
/// User-facing contract for subscribing to AeroDB events through the async daemon.
/// Implement this to do custom processing against ordered event streams.
/// </summary>
public interface IDaliSubscription
{
    /// <summary>Unique name used to track subscription progress.</summary>
    string SubscriptionName { get; }

    /// <summary>Version for blue/green subscription upgrades.</summary>
    uint SubscriptionVersion { get; }

    /// <summary>Fine-tune batch size and processing behavior.</summary>
    AsyncOptions Options { get; }

    /// <summary>Filter which event types this subscription handles.</summary>
    void Filter(IEventFilterable filterable);

    /// <summary>
    /// Process a batch of events. Called by the daemon with an open session.
    /// Use <paramref name="session"/> for reads/writes and
    /// <paramref name="controller"/> to report dead letters or failures.
    /// </summary>
    Task ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession session,
        CancellationToken cancellationToken);
}
