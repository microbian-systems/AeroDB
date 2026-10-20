using AeroDB.Sable;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Wolverine;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Bridges a AeroDB.Sable event subscription to Wolverine's message bus.
/// Creates a MessageContext, enlists in the outbox, delegates to the subscription,
/// then commits the session. The attached session listener marks durable state
/// and flushes messages only after that commit has succeeded.
/// </summary>
[Experimental("AERODB001")]
internal class AeroDBSubscriptionRunner
{
    private readonly IAeroDBSubscription _subscription;
    private readonly IWolverineRuntime _runtime;

    public AeroDBSubscriptionRunner(IAeroDBSubscription subscription, IWolverineRuntime runtime)
    {
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IAeroDBSubscription Subscription => _subscription;

    public string SubscriptionName => _subscription.SubscriptionName;
    public uint SubscriptionVersion => _subscription.SubscriptionVersion;
    public AsyncOptions Options => _subscription.Options;

    public async Task ProcessBatchAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (session.Listeners.OfType<FlushOutgoingMessagesOnAeroDBCommit>().Any())
        {
            throw new InvalidOperationException(
                "The document session is already enrolled in an AeroDB Wolverine outbox listener.");
        }

        // Create a Wolverine message context scoped to this batch
        var context = new MessageContext(_runtime);

        // Get the AeroDB.Sable message store and enlist in outbox
        var store = _runtime.Services.GetRequiredService<AeroDBMessageStore>();
        context.OverrideStorage(store);
        var ownerId = store.GetOwnerId();

        var transaction = new AeroDBEnvelopeTransaction(store, session, ownerId);
        var listener = new FlushOutgoingMessagesOnAeroDBCommit(context, store);
        listener.AttachTransaction(transaction);
        session.Listeners.Add(listener);
        await context.EnlistInOutboxAsync(transaction);

        // Delegate to the user's subscription logic
        await _subscription.ProcessEventsAsync(page, controller, session, ct);

        // Commit session changes (event writes, projected docs, etc.). The
        // session-scoped listener is the single post-commit authority.
        await session.SaveChangesAsync(ct);
    }
}
