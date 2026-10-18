using JasperFx.Descriptors;
using JasperFx;
using JasperFx.MultiTenancy;
using System.Collections.Immutable;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Prepares a staged tenant message store for Wolverine lifecycle operations
/// before discovery publishes the store to the dynamic tenant source.
/// </summary>
internal interface IAeroDBTenantMessageStoreReadiness
{
    Task PrepareAsync(IMessageStore store, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies Wolverine's public message-store initialization and schema policy to
/// one already-owned tenant pair. The runtime is resolved only when a refresh
/// prepares a pair, avoiding a registration-time dependency cycle.
/// </summary>
internal sealed class AeroDBTenantMessageStoreReadiness : IAeroDBTenantMessageStoreReadiness
{
    private readonly Func<IWolverineRuntime> _runtime;

    public AeroDBTenantMessageStoreReadiness(Func<IWolverineRuntime> runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task PrepareAsync(IMessageStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = _runtime() ?? throw new InvalidOperationException("The Wolverine runtime is unavailable.");
        store.Initialize(runtime);

        if (runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Dormant, server-owned tenant discovery. The source is the sole publisher of
/// its registry and sole caller of its pair cache; neither is exposed through
/// runtime registration. Live activation still requires recovery, persistent
/// location history and composite-cache deactivation semantics.
/// </summary>
internal sealed class AeroDBTenantedMessageSource : ITenantedMessageSource
{
    private readonly IAeroDBTenantStoreDirectory _directory;
    private readonly AeroDBTenantStoreRegistry _registry;
    private readonly AeroDBTenantStorePairCache _pairs;
    private readonly IAeroDBTenantMessageStoreReadiness _readiness;
    private readonly AeroDBMainStoreIdentity? _main;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot _snapshot = Snapshot.Empty;

    public AeroDBTenantedMessageSource(IAeroDBTenantStoreDirectory directory,
        AeroDBTenantStoreRegistry registry, AeroDBTenantStorePairCache pairs,
        AeroDBMainStoreIdentity? main,
        IAeroDBTenantMessageStoreReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(readiness);
        _directory = directory;
        _registry = registry;
        _pairs = pairs;
        _main = main;
        _readiness = readiness;
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || AeroDBTenantSelector.IsReserved(tenantId))
            throw new UnknownTenantIdException(tenantId ?? string.Empty);

        var current = Volatile.Read(ref _snapshot);
        if (current.ByTenant.TryGetValue(tenantId, out var store)) return store;

        // Misses sharing one snapshot coalesce behind its next successful
        // refresh. An absent tenant never falls back to the main database.
        await RefreshCoreAsync(current).ConfigureAwait(false);
        current = Volatile.Read(ref _snapshot);
        return current.ByTenant.TryGetValue(tenantId, out store)
            ? store
            : throw new UnknownTenantIdException(tenantId);
    }

    public Task RefreshAsync() => RefreshCoreAsync(null);

    // Discovery publishes complete stores, never a location-only partial view.
    public Task RefreshLiteAsync() => RefreshAsync();

    public IReadOnlyList<IMessageStore> AllActive() => Volatile.Read(ref _snapshot).Stores;

    public IReadOnlyList<Assignment<IMessageStore>> AllActiveByTenant() =>
        Volatile.Read(ref _snapshot).Assignments;

    private async Task RefreshCoreAsync(Snapshot? expected)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _snapshot);
            if (expected is not null && !ReferenceEquals(expected, previous)) return;

            var locations = await _directory.ListActiveAsync(CancellationToken.None).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(locations);
            var validated = AeroDBTenantStoreDirectorySnapshot.Create(locations);
            var ordered = validated.ByTenant.Values.OrderBy(location => location.TenantId, StringComparer.Ordinal)
                .ToImmutableArray();
            if (_main is not null && ordered.Any(_main.Matches))
                throw new InvalidOperationException("A tenant directory cannot map a tenant to the coordinator main database.");
            var stage = _registry.Stage(ordered);
            var stores = ImmutableArray.CreateBuilder<IMessageStore>(ordered.Length);
            var assignments = ImmutableArray.CreateBuilder<Assignment<IMessageStore>>(ordered.Length);
            var byTenant = ImmutableDictionary.CreateBuilder<string, IMessageStore>(StringComparer.Ordinal);
            foreach (var location in ordered)
            {
                var pair = await _pairs.GetStagedAsync(stage, location.TenantId).ConfigureAwait(false);
                if (!previous.ByTenant.TryGetValue(location.TenantId, out var existing))
                {
                    await _readiness.PrepareAsync(pair.DurableMessageStore).ConfigureAwait(false);
                }
                else if (!ReferenceEquals(existing, pair.DurableMessageStore))
                {
                    throw new InvalidOperationException(
                        "A previously published tenant changed its durable message-store instance.");
                }
                stores.Add(pair.DurableMessageStore);
                assignments.Add(new Assignment<IMessageStore>(location.TenantId, pair.DurableMessageStore));
                byTenant.Add(location.TenantId, pair.DurableMessageStore);
            }
            var next = new Snapshot(ordered, stores.ToImmutable(), assignments.ToImmutable(), byTenant.ToImmutable());
            // No candidate route is active until every pair and immutable view
            // is built. Failure leaves active routing unchanged, with historical
            // reservations retained to prevent later physical-store reuse.
            _registry.Activate(stage);
            Volatile.Write(ref _snapshot, next);
        }
        finally { _refreshGate.Release(); }
    }

    private sealed record Snapshot(
        ImmutableArray<AeroDBTenantStoreLocation> Locations,
        ImmutableArray<IMessageStore> Stores,
        ImmutableArray<Assignment<IMessageStore>> Assignments,
        ImmutableDictionary<string, IMessageStore> ByTenant)
    {
        public static Snapshot Empty { get; } = new([], [], [],
            ImmutableDictionary.Create<string, IMessageStore>(StringComparer.Ordinal));
    }
}
