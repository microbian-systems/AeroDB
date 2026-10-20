using AeroDB.Sable;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace AeroDB.WolverineFx;

/// <summary>
/// The resources dedicated to one validated physical tenant store. The document
/// store owns the underlying client; the paired message store borrows it.
/// </summary>
internal interface IAeroDBTenantStorePair : IAsyncDisposable
{
    AeroDBTenantStoreLocation Location { get; }

    IDocumentStore DocumentStore { get; }

    AeroDBMessageStore MessageStore { get; }

    AeroDBTenantMessageStore DurableMessageStore { get; }
}

/// <summary>
/// Creates resources only after the server-owned registry has selected a
/// validated location. This is internal so applications do not receive a
/// general-purpose client or endpoint factory.
/// </summary>
internal interface IAeroDBTenantStorePairFactory
{
    ValueTask<IAeroDBTenantStorePair> CreateAsync(
        AeroDBTenantStoreLocation location,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The document store is the sole client owner. The message store is disposed
/// first so it stops accepting work, but its explicit borrowed-client mode
/// cannot release the document store's client.
/// </summary>
internal sealed class AeroDBTenantStorePair : IAeroDBTenantStorePair
{
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public AeroDBTenantStorePair(
        AeroDBTenantStoreLocation location,
        IDocumentStore documentStore,
        AeroDBMessageStore messageStore)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        DocumentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        MessageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));

        if (MessageStore.OwnsClient)
        {
            throw new ArgumentException(
                "A tenant store pair requires a message store that borrows the document-store client.",
                nameof(messageStore));
        }

        if (!ReferenceEquals(DocumentStore.Client, MessageStore.Client))
        {
            throw new ArgumentException(
                "A tenant store pair requires its document store and message store to share one client instance.",
                nameof(messageStore));
        }

        DurableMessageStore = new AeroDBTenantMessageStore(location, messageStore);
    }

    public AeroDBTenantStoreLocation Location { get; }

    public IDocumentStore DocumentStore { get; }

    public AeroDBMessageStore MessageStore { get; }

    public AeroDBTenantMessageStore DurableMessageStore { get; }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeResourcesAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeResourcesAsync()
    {
        Exception? messageStoreFailure = null;
        try
        {
            await MessageStore.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            messageStoreFailure = exception;
        }

        try
        {
            await DocumentStore.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception documentStoreFailure)
        {
            if (messageStoreFailure is not null)
            {
                throw new AggregateException(
                    "Both tenant-store resources failed while being disposed.",
                    messageStoreFailure,
                    documentStoreFailure);
            }

            ExceptionDispatchInfo.Capture(documentStoreFailure).Throw();
        }

        if (messageStoreFailure is not null)
        {
            ExceptionDispatchInfo.Capture(messageStoreFailure).Throw();
        }
    }
}

/// <summary>
/// Cache of pairs by validated physical location. This dormant component is
/// intentionally not registered with Wolverine or any runtime recovery path.
/// </summary>
internal sealed class AeroDBTenantStorePairCache : IAsyncDisposable
{
    private readonly AeroDBTenantStoreRegistry _registry;
    private readonly IAeroDBTenantStorePairFactory _pairFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<IAeroDBTenantStorePair>>> _pairs =
        new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private Task? _disposeTask;

    public AeroDBTenantStorePairCache(
        AeroDBTenantStoreRegistry registry,
        IAeroDBTenantStorePairFactory pairFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pairFactory = pairFactory ?? throw new ArgumentNullException(nameof(pairFactory));
    }

    public ValueTask<IAeroDBTenantStorePair> GetAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
        => GetLocationAsync(_registry.Find(tenantId), cancellationToken);

    internal ValueTask<IAeroDBTenantStorePair> GetStagedAsync(
        AeroDBTenantStoreRegistry.StagedDirectory stage, string tenantId,
        CancellationToken cancellationToken = default)
        => GetLocationAsync(_registry.FindStaged(stage, tenantId), cancellationToken);

    private async ValueTask<IAeroDBTenantStorePair> GetLocationAsync(
        AeroDBTenantStoreLocation location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Lazy<Task<IAeroDBTenantStorePair>> pairTask;
        lock (_lifecycleGate)
        {
            ThrowIfClosing();
            pairTask = _pairs.GetOrAdd(
                location.PhysicalKey,
                _ => new Lazy<Task<IAeroDBTenantStorePair>>(
                    () => CreateValidatedPairAsync(location),
                    LazyThreadSafetyMode.ExecutionAndPublication));
        }

        // One request observes exactly one initializer generation. Eviction
        // permits a later request to recover; it never silently retries this one.
        var sharedTask = pairTask.Value;
        TrackFailedPairEviction(location.PhysicalKey, pairTask, sharedTask);
        try
        {
            return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            EvictFailedPair(location.PhysicalKey, pairTask);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleGate)
        {
            _disposeTask ??= DisposePairsAsync(_pairs.Values.ToArray());
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task<IAeroDBTenantStorePair> CreateValidatedPairAsync(AeroDBTenantStoreLocation location)
    {
        var pair = await _pairFactory.CreateAsync(location, CancellationToken.None).ConfigureAwait(false);
        if (pair is null)
        {
            throw new InvalidOperationException("The tenant store-pair factory returned no pair.");
        }

        if (pair.Location != location)
        {
            try
            {
                await pair.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposalFailure)
            {
                throw new AggregateException(
                    "The tenant store-pair factory returned a pair for another location and disposal failed.",
                    new InvalidOperationException("The tenant store-pair factory returned a pair for another location."),
                    disposalFailure);
            }

            throw new InvalidOperationException("The tenant store-pair factory returned a pair for another location.");
        }

        return pair;
    }

    private void TrackFailedPairEviction(
        string physicalKey,
        Lazy<Task<IAeroDBTenantStorePair>> pairTask,
        Task<IAeroDBTenantStorePair> sharedTask)
    {
        _ = sharedTask.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted || completed.IsCanceled)
                {
                    // All waiters may have cancelled before initialization
                    // fails. Observe the shared failure without logging details.
                    _ = completed.Exception;
                    EvictFailedPair(physicalKey, pairTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void EvictFailedPair(
        string physicalKey,
        Lazy<Task<IAeroDBTenantStorePair>> pairTask)
    {
        if (!pairTask.IsValueCreated)
        {
            return;
        }

        var sharedTask = pairTask.Value;
        if (!sharedTask.IsFaulted && !sharedTask.IsCanceled)
        {
            return;
        }

        var entry = new KeyValuePair<string, Lazy<Task<IAeroDBTenantStorePair>>>(physicalKey, pairTask);
        ((ICollection<KeyValuePair<string, Lazy<Task<IAeroDBTenantStorePair>>>>)_pairs).Remove(entry);
    }

    private async Task DisposePairsAsync(IEnumerable<Lazy<Task<IAeroDBTenantStorePair>>> pairTasks)
    {
        List<Exception>? failures = null;

        foreach (var pairTask in pairTasks)
        {
            try
            {
                var pair = await pairTask.Value.ConfigureAwait(false);
                await pair.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        _pairs.Clear();
        if (failures is null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("One or more tenant store pairs failed while being disposed.", failures);
    }

    private void ThrowIfClosing()
    {
        if (_disposeTask is not null)
        {
            throw new ObjectDisposedException(nameof(AeroDBTenantStorePairCache));
        }
    }
}
