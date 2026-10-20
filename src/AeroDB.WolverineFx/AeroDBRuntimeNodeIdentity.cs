using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using RuntimeWolverineNode = global::Wolverine.Runtime.Agents.WolverineNode;

namespace AeroDB.WolverineFx;

/// <summary>
/// The process-local view of the Wolverine node that owns durable envelopes.
/// A composite main store and every lazily-created tenant store intentionally
/// share one instance. The value is not captured during initialization because
/// Balanced mode assigns the real node number later through PersistAsync.
/// </summary>
internal sealed class AeroDBRuntimeNodeIdentity
{
    private readonly object _gate = new();
    private IWolverineRuntime? _runtime;
    private Guid? _persistedNodeId;
    private int _persistedOwnerId;

    public void BindRuntime(IWolverineRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_runtime is not null && !ReferenceEquals(_runtime, runtime))
                throw new InvalidOperationException("An AeroDB durable store cannot be rebound to another Wolverine runtime.");

            if (_persistedNodeId is Guid nodeId && nodeId != runtime.Options.UniqueNodeId)
                throw new InvalidOperationException("The persisted AeroDB node identity does not match the Wolverine runtime identity.");

            _runtime = runtime;
        }
    }

    public void RecordPersistedNode(RuntimeWolverineNode node, int assignedOwnerId)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (assignedOwnerId <= 0)
            throw new InvalidOperationException("A persisted Wolverine node must have a positive database-assigned owner number.");

        lock (_gate)
        {
            ValidateNode(node);

            if (_persistedOwnerId != 0 && _persistedOwnerId != assignedOwnerId)
                throw new InvalidOperationException("An AeroDB durable store cannot be rebound to another database-assigned owner number.");

            _persistedNodeId = node.NodeId;
            _persistedOwnerId = assignedOwnerId;
        }
    }

    public void ValidateNode(RuntimeWolverineNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            if (_runtime is not null && _runtime.Options.UniqueNodeId != node.NodeId)
                throw new InvalidOperationException("The persisted Wolverine node does not belong to the bound runtime.");
            if (_persistedNodeId is Guid existingNodeId && existingNodeId != node.NodeId)
                throw new InvalidOperationException("An AeroDB durable store cannot be rebound to another Wolverine node.");
        }
    }

    public int GetOwnerId()
    {
        lock (_gate)
        {
            if (_runtime?.Options.Durability.Mode == DurabilityMode.Solo)
                return 1;

            if (_runtime?.Options.Durability.Mode == DurabilityMode.Balanced)
            {
                if (_persistedOwnerId <= 0)
                    throw new InvalidOperationException("Balanced Wolverine durability has no database-assigned owner number yet.");
                return _persistedOwnerId;
            }

            return _runtime?.Options.Durability.AssignedNodeNumber ?? _persistedOwnerId;
        }
    }

    public Guid GetRequiredNodeId()
    {
        lock (_gate)
        {
            if (_persistedNodeId is Guid nodeId)
                return nodeId;

            throw new InvalidOperationException("Leadership requires a successfully persisted Wolverine node identity.");
        }
    }

    public TimeSpan GetLeaseTimeout()
    {
        lock (_gate)
        {
            var durability = _runtime?.Options.Durability
                ?? throw new InvalidOperationException("Leadership requires a bound Wolverine runtime.");
            if (durability.StaleNodeTimeout <= TimeSpan.Zero || durability.HealthCheckPollingTime <= TimeSpan.Zero ||
                durability.HealthCheckPollingTime >= durability.StaleNodeTimeout)
                throw new InvalidOperationException("Wolverine leadership requires a positive stale-node timeout greater than the health-check polling interval.");
            return durability.StaleNodeTimeout;
        }
    }
}
