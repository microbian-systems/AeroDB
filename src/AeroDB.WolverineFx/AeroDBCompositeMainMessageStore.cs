using JasperFx.Descriptors;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace AeroDB.WolverineFx;

/// <summary>
/// Main-store facade used only inside a Wolverine multi-tenant composite.
/// The underlying main store owns neither its client nor its disposal through
/// this facade; its document-store lifetime remains outside composite recovery.
/// </summary>
internal sealed class AeroDBCompositeMainMessageStore : IMessageStoreWithAgentSupport
{
    private readonly AeroDBMessageStore _inner;
    private readonly object _agentGate = new();
    private AeroDBDurabilityAgent? _agent;
    private IWolverineRuntime? _agentRuntime;

    public AeroDBCompositeMainMessageStore(AeroDBMessageStore inner, AeroDBMainStoreIdentity identity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (_inner.OwnsClient)
            throw new ArgumentException("A composite main store must borrow its document-store client.", nameof(inner));
        Uri = Identity.Uri;
    }

    public MessageStoreRole Role => MessageStoreRole.Main;
    public List<string> TenantIds => _inner.TenantIds;
    internal AeroDBMainStoreIdentity Identity { get; }
    public Uri Uri { get; }
    public bool HasDisposed => _inner.HasDisposed;
    public IMessageInbox Inbox => _inner.Inbox;
    public IMessageOutbox Outbox => _inner.Outbox;
    public INodeAgentPersistence Nodes => _inner.Nodes;
    public IListenerStore Listeners => _inner.Listeners;
    public IMessageStoreAdmin Admin => _inner.Admin;
    public IDeadLetters DeadLetters => _inner.DeadLetters;
    public IScheduledMessages ScheduledMessages => _inner.ScheduledMessages;
    public string Name => _inner.Name;

    public IAgent BuildAgent(IWolverineRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_agentGate)
        {
            if (_agent is not null)
            {
                if (!ReferenceEquals(_agentRuntime, runtime))
                    throw new InvalidOperationException("A composite main durability agent cannot be rebound to another Wolverine runtime.");

                return _agent;
            }

            _agentRuntime = runtime;
            return _agent = new AeroDBDurabilityAgent(Uri, _inner, runtime);
        }
    }

    public void Initialize(IWolverineRuntime runtime) => _inner.Initialize(runtime);
    public DatabaseDescriptor Describe() => _inner.Describe();
    public Task DrainAsync() => _inner.DrainAsync();
    public IAgent StartScheduledJobs(IWolverineRuntime runtime) => _inner.StartScheduledJobs(runtime);
    public Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit) =>
        _inner.LoadPageOfGloballyOwnedIncomingAsync(listenerAddress, limit);
    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming) =>
        _inner.ReassignIncomingAsync(ownerId, incoming);
    public void PromoteToMain(IWolverineRuntime runtime) => _inner.PromoteToMain(runtime);
    public void DemoteToAncillary() => _inner.DemoteToAncillary();

    // MessageStoreCollection disposes stores it discovers. This facade borrows
    // the raw main store, whose document-store client has an independent owner.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
