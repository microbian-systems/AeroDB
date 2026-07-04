using AeroDB;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;

namespace Dali.WolverineFx;

/// <summary>
/// Bridges a AeroDB event subscription to Wolverine's message bus.
/// Creates a MessageContext, enlists in the outbox, delegates to the subscription,
/// then commits the session and flushes outgoing messages.
/// </summary>
internal class DaliSubscriptionRunner
{
    private readonly IDaliSubscription _subscription;
    private readonly IWolverineRuntime _runtime;

    public DaliSubscriptionRunner(IDaliSubscription subscription, IWolverineRuntime runtime)
    {
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IDaliSubscription Subscription => _subscription;

    public string SubscriptionName => _subscription.SubscriptionName;
    public uint SubscriptionVersion => _subscription.SubscriptionVersion;
    public AsyncOptions Options => _subscription.Options;

    public async Task ProcessBatchAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession session,
        CancellationToken ct)
    {
        // Create a Wolverine message context scoped to this batch
        var context = new MessageContext(_runtime);

        // Get the AeroDB message store and enlist in outbox
        var store = _runtime.Services.GetRequiredService<DaliMessageStore>();
        context.OverrideStorage(store);
        var ownerId = store.GetOwnerId();

        await context.EnlistInOutboxAsync(new DaliEnvelopeTransaction(store, session, ownerId));

        // Delegate to the user's subscription logic
        await _subscription.ProcessEventsAsync(page, controller, session, ct);

        // Commit session changes (event writes, projected docs, etc.)
        await session.SaveChangesAsync(ct);

        // Flush any Wolverine messages published by the subscription
        await context.FlushOutgoingMessagesAsync();
    }
}
