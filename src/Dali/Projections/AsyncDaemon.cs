using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Reports progress during projection rebuilds.
/// </summary>
/// <param name="projectionName">The name of the projection being rebuilt.</param>
/// <param name="current">The current sequence position.</param>
/// <param name="total">The total number of events to process.</param>
public delegate void ProjectionRebuildProgressCallback(string projectionName, long current, long total);

/// <summary>
/// Background worker that polls for new events and applies async projections.
/// Runs each projection in its own shard with independent health and progress tracking,
/// and persists per-projection watermarks to the mt_projection_progress table for restart resilience.
/// </summary>
public class AsyncDaemon : IAsyncDisposable
{
    private readonly IDocumentStore _store;
    private readonly IReadOnlyList<IProjection> _projections;
    private readonly ILogger<AsyncDaemon> _logger;
    private static readonly System.Text.Json.JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private readonly List<ProjectionShard> _shards = new();
    private readonly List<Task> _shardTasks = new();
    private readonly CancellationTokenSource _cts = new();
    private TimeSpan _pollInterval;
    private bool _disposed;

    /// <summary>
    /// Current health state of the daemon. Updated on each poll cycle from shard states.
    /// </summary>
    public DaemonHealthState Health { get; private set; } = new(false, null, null, 0, 0, null);

    /// <summary>
    /// Read-only collection of all registered shards.
    /// </summary>
    public IReadOnlyList<ProjectionShard> Shards => _shards.AsReadOnly();

    /// <summary>
    /// Optional callback invoked during projection rebuilds to report progress.
    /// </summary>
    public ProjectionRebuildProgressCallback? RebuildProgress { get; set; }

    public AsyncDaemon(IDocumentStore store, IReadOnlyList<IProjection> projections, ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _logger = loggerFactory?.CreateLogger<AsyncDaemon>()
            ?? NullLogger<AsyncDaemon>.Instance;
    }

    /// <summary>
    /// Starts the background polling loop. Creates one shard per async/live projection
    /// and launches all shard workers in parallel.
    /// </summary>
    public void Start(TimeSpan pollInterval)
    {
        lock (_lock)
        {
            if (_shardTasks.Count > 0)
                throw new InvalidOperationException("AsyncDaemon is already running.");

            _pollInterval = pollInterval;
            Health = Health with { IsRunning = true };

            // Create one shard per async/live projection
            var asyncProjections = _projections
                .Where(p => p.Lifecycle == ProjectionLifecycle.Async || p.Lifecycle == ProjectionLifecycle.Live)
                .ToList();

            foreach (var proj in asyncProjections)
            {
                var shard = new ProjectionShard(proj.Name, proj);
                shard.IsActive = true;
                shard.Health = shard.Health with { IsRunning = true };
                _shards.Add(shard);
                _shardTasks.Add(RunShardAsync(shard, pollInterval, _cts.Token));
            }

            _logger.LogInformation("AsyncDaemon started {Count} shards with poll interval {PollInterval}",
                _shards.Count, pollInterval);
        }
    }

    /// <summary>
    /// Signals the background loop to stop and awaits completion of all shards.
    /// </summary>
    public async Task StopAsync()
    {
        // Cancel all shard tasks before awaiting them.
        // Cancel() may throw AggregateException if linked CTS instances
        // are already disposed; swallow all cancellation-related errors.
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }

        foreach (var shard in _shards)
            shard.IsActive = false;

        if (_shardTasks.Count > 0)
        {
            try { await Task.WhenAll(_shardTasks).ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        _shardTasks.Clear();

        foreach (var shard in _shards)
            shard.Health = shard.Health with { IsRunning = false };

        Health = Health with { IsRunning = false };
        _logger.LogInformation("AsyncDaemon stopped");
    }

    /// <summary>
    /// Start a specific projection agent by shard name.
    /// </summary>
    public Task StartAgentAsync(string shardName, CancellationToken ct = default)
    {
        var shard = _shards.FirstOrDefault(s => s.Name == shardName);
        if (shard is null) throw new InvalidOperationException($"No shard found with name '{shardName}'.");
        if (shard.IsActive) return Task.CompletedTask;

        shard.IsActive = true;
        shard.Health = shard.Health with { IsRunning = true };
        var task = RunShardAsync(shard, _pollInterval, _cts.Token);
        lock (_lock)
        {
            _shardTasks.Add(task);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop a specific projection agent by shard name.
    /// </summary>
    public Task StopAgentAsync(string shardName)
    {
        var shard = _shards.FirstOrDefault(s => s.Name == shardName);
        if (shard is null || !shard.IsActive) return Task.CompletedTask;

        shard.IsActive = false;
        shard.Health = shard.Health with { IsRunning = false };
        return Task.CompletedTask;
    }

    /// <summary>
    /// Per-shard background loop that polls for new events matching the shard's projection.
    /// </summary>
    private async Task RunShardAsync(ProjectionShard shard, TimeSpan pollInterval, CancellationToken ct)
    {
        var projection = shard.Projection;
        var projectionName = shard.Name;

        // Load initial watermark for this shard
        if (_store.Options.ProjectionBuild.EnsureStateTable)
        {
            try
            {
                await using var initSession = await _store.LightweightSessionAsync().ConfigureAwait(false);
                var internalSession = (InternalSessionBase)initSession;
                var response = await internalSession.Session.RawQuery(
                    $"SELECT * FROM mt_projection_progress WHERE projection_name = '{projectionName}'",
                    null, ct).ConfigureAwait(false);

                if (!response.HasErrors && response.Count > 0)
                {
                    var raw = response.GetValue<List<object>>(0);
                    if (raw is { Count: > 0 })
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(raw);
                        var progress = System.Text.Json.JsonSerializer
                            .Deserialize<List<ProjectionProgress>>(json, _snakeOptions);
                        if (progress is { Count: > 0 } && progress[0].LastVersion > 0)
                            shard.Watermark = progress[0].LastVersion;
                    }
                }

                _logger.LogInformation("Shard {ShardName}: loaded watermark = {Watermark}",
                    projectionName, shard.Watermark);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shard {ShardName}: could not load projection progress (table may not exist yet)",
                    projectionName);
            }
        }

        while (!ct.IsCancellationRequested && shard.IsActive)
        {
            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !shard.IsActive) break;

                await using var session = await _store.LightweightSessionAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                // Fetch events after this shard's watermark
                var newEvents = await session.Events.FetchAllAfterSequence(shard.Watermark, ct).ConfigureAwait(false);

                if (newEvents.Count == 0) continue;

                _logger.LogDebug("Shard {ShardName}: fetched {Count} new events after sequence {Sequence}",
                    projectionName, newEvents.Count, shard.Watermark);

                // Process only events matching this projection's event types
                var matchingEvents = newEvents
                    .Where(e => e.Data is not null && projection.EventTypes.Contains(e.Data.GetType()))
                    .ToList();

                if (matchingEvents.Count == 0) continue;

                _logger.LogInformation("Shard {ShardName}: processing {Count} matching events",
                    projectionName, matchingEvents.Count);

                // Enrichment hook
                if (projection is IEnrichProjection enricher)
                    await enricher.EnrichAsync(session, matchingEvents.AsReadOnly(), ct).ConfigureAwait(false);

                // Build a context from all matching events (not grouped by stream — the
                // projection's ApplyAsync handles grouping internally)
                var context = new ProjectionContext(session, matchingEvents.AsReadOnly());
                await projection.ApplyAsync(context, ct).ConfigureAwait(false);

                // Update watermark
                var maxSeq = matchingEvents.Max(e => e.Sequence);
                if (maxSeq > shard.Watermark)
                    shard.Watermark = maxSeq;

                // Persist watermark
                if (_store.Options.ProjectionBuild.EnsureStateTable)
                {
                    var sql =
                        $"UPSERT mt_projection_progress:`{projectionName}` CONTENT {{ projection_name: '{projectionName}', last_version: {shard.Watermark}, last_updated: time::now() }}";
                    await session.ExecuteSqlAsync(sql, null, CancellationToken.None).ConfigureAwait(false);
                }

                // Before Commit: invoke daemon-level change listeners
                if (_store.Options.ChangeListeners.Count > 0)
                {
                    var asyncProjections = _shards
                        .Where(s => s.IsActive)
                        .Select(s => s.Projection)
                        .ToList();
                    foreach (var listener in _store.Options.ChangeListeners)
                        await listener.BeforeCommitAsync(session, asyncProjections, CancellationToken.None).ConfigureAwait(false);
                }

                await session.SaveChangesAsync().ConfigureAwait(false);

                // After Commit: invoke daemon-level change listeners
                if (_store.Options.ChangeListeners.Count > 0)
                {
                    var asyncProjections = _shards
                        .Where(s => s.IsActive)
                        .Select(s => s.Projection)
                        .ToList();
                    foreach (var listener in _store.Options.ChangeListeners)
                        await listener.AfterCommitAsync(session, asyncProjections, CancellationToken.None, 0).ConfigureAwait(false);
                }

                // Update shard health
                shard.Health = shard.Health with
                {
                    LastSuccess = DateTimeOffset.UtcNow,
                    HighWaterSequence = shard.Watermark,
                    LagCount = 0
                };

                // Update daemon-level health as aggregate across all shards
                UpdateDaemonHealth();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shard {ShardName} cycle failed; will retry on next poll", projectionName);
                shard.Health = shard.Health with
                {
                    LastError = DateTimeOffset.UtcNow,
                    LastException = ex.Message
                };
            }
        }

        _logger.LogInformation("Shard {ShardName} background loop stopped", projectionName);
    }

    /// <summary>
    /// Update the daemon-level health as an aggregate of all shard states.
    /// </summary>
    private void UpdateDaemonHealth()
    {
        var activeShards = _shards.Where(s => s.IsActive).ToList();
        if (activeShards.Count == 0)
        {
            Health = new DaemonHealthState(false, null, null, 0, 0, null);
            return;
        }

        Health = new DaemonHealthState(
            IsRunning: activeShards.Any(s => s.Health.IsRunning),
            LastSuccess: activeShards.Max(s => s.Health.LastSuccess),
            LastError: activeShards.Max(s => s.Health.LastError),
            HighWaterSequence: activeShards.Max(s => s.Watermark),
            LagCount: 0,
            LastException: activeShards
                .Where(s => s.Health.LastException is not null)
                .Select(s => s.Health.LastException)
                .FirstOrDefault()
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel shard tasks and await them without relying on StopAsync
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }

        foreach (var shard in _shards)
            shard.IsActive = false;

        if (_shardTasks.Count > 0)
        {
            try { await Task.WhenAll(_shardTasks).ConfigureAwait(false); }
            catch { /* swallow */ }
        }

        _shardTasks.Clear();

        foreach (var shard in _shards)
            shard.Health = shard.Health with { IsRunning = false };

        Health = Health with { IsRunning = false };

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
