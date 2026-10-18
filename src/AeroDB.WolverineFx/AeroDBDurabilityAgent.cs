using JasperFx;
using JasperFx.Descriptors;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace AeroDB.WolverineFx;

/// <summary>
/// Tenant-scoped facade for Wolverine agent discovery. The underlying store
/// remains the raw persistence implementation used by fixed outboxed sessions.
/// This facade is deliberately constructed only by an immutable tenant pair.
/// </summary>
internal sealed class AeroDBTenantMessageStore : IMessageStoreWithAgentSupport
{
    private readonly AeroDBTenantStoreLocation _location;
    private readonly AeroDBMessageStore _inner;
    private readonly object _agentGate = new();
    private AeroDBDurabilityAgent? _agent;
    private IWolverineRuntime? _agentRuntime;

    public AeroDBTenantMessageStore(AeroDBTenantStoreLocation location, AeroDBMessageStore inner)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        if (_inner.Uri != _location.PhysicalIdentity)
            throw new ArgumentException("The tenant message store must use the pair's physical identity.", nameof(inner));
    }

    public MessageStoreRole Role => MessageStoreRole.Tenant;
    public List<string> TenantIds => _inner.TenantIds;
    public Uri Uri => _location.PhysicalIdentity;
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
                    throw new InvalidOperationException("A tenant durability agent cannot be rebound to another Wolverine runtime.");
                return _agent;
            }

            _agentRuntime = runtime;
            return _agent = new AeroDBDurabilityAgent(_location.PhysicalIdentity, _inner, runtime);
        }
    }

    public void Initialize(IWolverineRuntime runtime) => _inner.Initialize(runtime);
    public DatabaseDescriptor Describe() => _inner.Describe();
    public Task DrainAsync() => _inner.DrainAsync();
    public IAgent StartScheduledJobs(IWolverineRuntime runtime) => _inner.StartScheduledJobs(runtime);
    public Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit) =>
        _inner.LoadPageOfGloballyOwnedIncomingAsync(listenerAddress, limit);
    public Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming) => _inner.ReassignIncomingAsync(ownerId, incoming);

    // A tenant facade must never be escalated to the global coordinating store.
    public void PromoteToMain(IWolverineRuntime runtime) =>
        throw new InvalidOperationException("A tenant message store cannot be promoted to the main store.");
    public void DemoteToAncillary() { }
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// Wolverine-owned, dormant recovery loop for one immutable tenant physical
/// store. No registration is made here; the runtime controls the agent life
/// cycle when a future composite registration is explicitly approved.
/// </summary>
internal sealed class AeroDBDurabilityAgent : IAgent
{
    private readonly Uri _uri;
    private readonly AeroDBMessageStore? _store;
    private readonly IWolverineRuntime? _runtime;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task>? _testRecovery;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private Task? _stopTask;
    private int _recoveryFailure;

    internal AeroDBDurabilityAgent(Uri uri, AeroDBMessageStore store, IWolverineRuntime runtime)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = runtime.Logger;
    }

    // Narrow deterministic seam for lifecycle tests. Production uses the
    // constructor above and always executes Wolverine recovery commands.
    internal AeroDBDurabilityAgent(Uri uri, Func<CancellationToken, Task> recovery)
    {
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _testRecovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    }

    public Uri Uri => _uri;
    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
    public string Description => "AeroDB tenant durability recovery agent";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_loop is not null)
                return Task.CompletedTask;

            _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = RunAsync(_stopping.Token);
            Status = AgentStatus.Running;
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_loop is null)
                return;

            if (_stopTask is null)
            {
                // Publish the generation's completion before starting the
                // asynchronous drain. A naturally completed loop can make an
                // async method finish synchronously; assigning after invoking
                // it would otherwise resurrect a stale completed stop task.
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stopTask = completion.Task;
                _stopTask = stopTask;
                _ = StopLoopAsync(_loop, _stopping, completion);
            }
            else
            {
                stopTask = _stopTask;
            }
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopLoopAsync(
        Task loop,
        CancellationTokenSource? stopping,
        TaskCompletionSource completion)
    {
        try
        {
            stopping?.Cancel();
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping?.IsCancellationRequested == true)
        {
            // Expected shutdown of the owned loop.
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_loop, loop))
                {
                    _loop = null;
                    _stopping = null;
                    Status = AgentStatus.Stopped;
                }

                if (ReferenceEquals(_stopTask, completion.Task))
                    _stopTask = null;
            }

            stopping?.Dispose();
            completion.TrySetResult();
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_lifecycleGate) loop = _loop;

        return Task.FromResult(Status != AgentStatus.Running || loop is null || loop.IsCompleted
            ? HealthCheckResult.Unhealthy("Tenant durability recovery agent is not running.")
            : Volatile.Read(ref _recoveryFailure) == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Tenant durability recovery requires operator attention."));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = _runtime?.Options.Durability;
        if (settings is not null && settings.FirstNodeReassignmentExecution > TimeSpan.Zero)
            await Task.Delay(settings.FirstNodeReassignmentExecution, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await RecoverOnceAsync(cancellationToken).ConfigureAwait(false);
            var interval = settings?.NodeReassignmentPollingTime ?? Timeout.InfiniteTimeSpan;
            if (interval == Timeout.InfiniteTimeSpan)
                return;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _tickGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            if (_testRecovery is not null)
            {
                await _testRecovery(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _recoveryFailure, 0);
                return;
            }

            // Recovery commands assign work to Wolverine's current node. A
            // missing or invalid assignment is not authority to release or
            // claim tenant work.
            if (_runtime!.Options.Durability.AssignedNodeNumber <= 0)
            {
                Volatile.Write(ref _recoveryFailure, 1);
                return;
            }

            var authorityTrusted = await ReleaseAbsentOwnersAsync(cancellationToken).ConfigureAwait(false);
            var incomingFailed = await RecoverIncomingAsync(cancellationToken).ConfigureAwait(false);
            var outgoingFailed = await RecoverOutgoingAsync(cancellationToken).ConfigureAwait(false);
            // Main-node authority is required only to reclaim work from absent
            // owners. Owner-zero recovery is safe without that decision and
            // must continue, while health still exposes the untrusted tick.
            Volatile.Write(ref _recoveryFailure,
                !authorityTrusted || incomingFailed || outgoingFailed ? 1 : 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Volatile.Write(ref _recoveryFailure, 1);
            _logger?.LogWarning("Tenant durability recovery tick failed.");
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task<bool> ReleaseAbsentOwnersAsync(CancellationToken cancellationToken)
    {
        var activeOwners = await TryReadActiveOwnersAsync(cancellationToken).ConfigureAwait(false);
        if (activeOwners is null)
            return false;

        var owners = await _store!.FindDistinctOwnerIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var owner in owners.Where(owner => owner > 0 && !activeOwners.Contains(owner)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _store.ReleaseAllOwnershipAsync(owner, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<HashSet<int>?> TryReadActiveOwnersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nodes = await _runtime!.Storage.Nodes.LoadAllNodesAsync(cancellationToken).ConfigureAwait(false);
            if (nodes is null || nodes.Count == 0)
                return null;

            var authorityOwners = nodes.Select(node => node.AssignedNodeNumber).ToArray();
            if (authorityOwners.Any(owner => owner <= 0)
                || authorityOwners.Distinct().Count() != authorityOwners.Length)
                return null;

            var activeOwners = authorityOwners.ToHashSet();
            activeOwners.Add(_runtime.Options.Durability.AssignedNodeNumber);
            return activeOwners;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Main-store authority was unavailable. Fail closed: no tenant
            // ownership is released on an untrusted or empty authority read.
            _logger?.LogWarning("Tenant recovery skipped owner release because main-node authority was unavailable.");
            return null;
        }
    }

    private async Task<bool> RecoverIncomingAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        var counts = await _store!.FindGloballyOwnedIncomingCountsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var count in counts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var circuit = _runtime!.Endpoints.FindListenerCircuit(count.Destination);
                if (circuit is null)
                    continue;
                await ExecuteIncomingCommandAsync(new RecoverIncomingMessagesCommand(
                    _store, count, circuit, _runtime.Options.Durability, _logger!), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                failed = true;
                _logger?.LogWarning("Tenant incoming recovery failed for one destination.");
            }
        }
        return failed;
    }

    private async Task<bool> RecoverOutgoingAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        var destinations = await _store!.FindGloballyOwnedOutgoingDestinationsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var destination in destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sendingAgent = _runtime!.Endpoints.GetOrBuildSendingAgent(destination);
                await new RecoverOutgoingMessagesCommand(sendingAgent, _store, _logger!)
                    .ExecuteAsync(_runtime, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                failed = true;
                _logger?.LogWarning("Tenant outgoing recovery failed for one destination.");
            }
        }
        return failed;
    }

    private async Task ExecuteIncomingCommandAsync(IAgentCommand command, CancellationToken cancellationToken)
    {
        var commands = await command.ExecuteAsync(_runtime!, cancellationToken).ConfigureAwait(false);
        while (commands.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            commands = await commands.Pop().ExecuteAsync(_runtime!, cancellationToken).ConfigureAwait(false);
        }
    }
}
