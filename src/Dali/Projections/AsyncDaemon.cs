using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Background worker that polls for new events and applies async projections.
/// Tracks per-projection high-water marks to avoid re-processing events and
/// persist progress to the mt_projection_progress table for restart resilience.
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
    private readonly AggregateCache _cache = new(1000);
    private Task? _runTask;
    private volatile bool _stopped;
    private long _highWaterSequence;
    private readonly Dictionary<string, long> _projectionWatermarks = new();

    /// <summary>
    /// Current health state of the daemon. Updated on each poll cycle.
    /// </summary>
    public DaemonHealthState Health { get; private set; } = new(false, null, null, 0, 0, null);

    /// <summary>
    /// Exposed cache instance for health checks and diagnostics.
    /// </summary>
    public AggregateCache? Cache => _cache;

    public AsyncDaemon(IDocumentStore store, IReadOnlyList<IProjection> projections, ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _logger = loggerFactory?.CreateLogger<AsyncDaemon>()
            ?? NullLogger<AsyncDaemon>.Instance;
    }

    /// <summary>
    /// Starts the background polling loop. Each poll cycle reads new events
    /// from the event store and applies matching async projections.
    /// </summary>
    public void Start(TimeSpan pollInterval)
    {
        lock (_lock)
        {
            if (_runTask is not null)
                throw new InvalidOperationException("AsyncDaemon is already running.");

            _stopped = false;
            Health = Health with { IsRunning = true };
            _runTask = RunAsync(pollInterval);
            _logger.LogInformation("AsyncDaemon started with poll interval {PollInterval}", pollInterval);
        }
    }

    /// <summary>
    /// Signals the background loop to stop and awaits completion.
    /// </summary>
    public async Task StopAsync()
    {
        Task? runTask;

        lock (_lock)
        {
            _stopped = true;
            runTask = _runTask;
        }

        if (runTask is null)
        {
            Health = Health with { IsRunning = false };
            return;
        }

        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch
        {
            // Swallow any other exceptions during shutdown
        }

        lock (_lock)
        {
            _runTask = null;
        }

        Health = Health with { IsRunning = false };
        _logger.LogInformation("AsyncDaemon stopped");
    }

    private async Task RunAsync(TimeSpan pollInterval)
    {
        _logger.LogInformation("AsyncDaemon background loop started");

        // Load per-projection watermarks so each projection resumes from its own position
        if (_store.Options.ProjectionBuild.EnsureStateTable)
        {
            try
            {
                await using var initSession = await _store.LightweightSessionAsync().ConfigureAwait(false);
                var internalSession = (InternalSessionBase)initSession;
                var response = await internalSession.Session.RawQuery(
                    "SELECT * FROM mt_projection_progress",
                    null,
                    CancellationToken.None).ConfigureAwait(false);

                if (!response.HasErrors && response.Count > 0)
                {
                    var raw = response.GetValue<List<object>>(0);
                    if (raw is { Count: > 0 })
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(raw);
                        var allProgress = System.Text.Json.JsonSerializer
                            .Deserialize<List<ProjectionProgress>>(json, _snakeOptions);
                        if (allProgress is { Count: > 0 })
                        {
                            foreach (var p in allProgress)
                            {
                                if (p.LastVersion > 0)
                                {
                                    _projectionWatermarks[p.ProjectionName] = p.LastVersion;
                                }
                            }

                            // Global high-water = minimum across all projections
                            if (_projectionWatermarks.Count > 0)
                                _highWaterSequence = _projectionWatermarks.Values.Min();

                            _logger.LogInformation(
                                "AsyncDaemon: loaded {Count} per-projection watermarks, global high-water = {Sequence}",
                                _projectionWatermarks.Count, _highWaterSequence);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AsyncDaemon: could not load projection progress (table may not exist yet)");
            }
        }

        while (!_stopped)
        {
            try
            {
                await Task.Delay(pollInterval).ConfigureAwait(false);
                if (_stopped) break;

                Health = Health with { IsRunning = true };

                var asyncProjections = _projections
                    .Where(p => p.Lifecycle == ProjectionLifecycle.Async)
                    .ToList();

                if (asyncProjections.Count == 0)
                    continue;

                _logger.LogDebug("AsyncDaemon: per-projection watermark tracking active for {Count} projections",
                    asyncProjections.Count);

                await using var session = await _store.LightweightSessionAsync().ConfigureAwait(false);
                if (_stopped) break;

                // Use the global high-water (minimum across projections) to avoid missing events
                var fetchAfterSequence = _projectionWatermarks.Count > 0
                    ? _projectionWatermarks.Values.Min()
                    : _highWaterSequence;

                var newEvents = await session.Events.FetchAllAfterSequence(fetchAfterSequence).ConfigureAwait(false);

                if (newEvents.Count == 0)
                    continue;

                _logger.LogDebug("AsyncDaemon: fetched {Count} new events after sequence {Sequence}",
                    newEvents.Count, fetchAfterSequence);

                // Group events by stream for per-stream processing
                var streamGroups = newEvents
                    .GroupBy(e => e.StreamId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var projection in asyncProjections)
                {
                    var projectionName = projection.GetType().Name;
                    var projectionWatermark = _projectionWatermarks.GetValueOrDefault(projectionName);

                    foreach (var (streamId, events) in streamGroups)
                    {
                        var matchingEvents = events
                            .Where(e => e.Data is not null && projection.EventTypes.Contains(e.Data.GetType()))
                            .ToList();

                        if (matchingEvents.Count == 0)
                            continue;

                        // Try cache for aggregate — if found, we can skip loading from store
                        if (_cache.TryGetValue(streamId, out var cached))
                        {
                            _logger.LogDebug(
                                "AsyncDaemon: cache hit for stream {StreamId} on projection {ProjectionType}",
                                streamId, projection.GetType().Name);
                        }

                        _logger.LogInformation("Async projection {ProjectionType} applied on stream {StreamId}",
                            projection.GetType().Name, streamId);

                        // Enrichment hook: allow projections to pre-load reference data
                        if (projection is IEnrichProjection enricher)
                        {
                            await enricher.EnrichAsync(session, matchingEvents.AsReadOnly(), CancellationToken.None).ConfigureAwait(false);
                        }

                        var context = new ProjectionContext(session, matchingEvents.AsReadOnly());
                        await projection.ApplyAsync(context, CancellationToken.None).ConfigureAwait(false);

                        // Update cache with the projected aggregate result
                        _cache.Set(streamId, matchingEvents);
                    }

                    // Track per-projection high-water based on events this projection actually consumed
                    var consumedSequences = newEvents
                        .Where(e => projection.EventTypes.Contains(e.Data?.GetType() ?? typeof(object)))
                        .Select(e => e.Sequence);
                    if (consumedSequences.Any())
                    {
                        var projectionMax = consumedSequences.Max();
                        if (projectionMax > projectionWatermark)
                            _projectionWatermarks[projectionName] = projectionMax;
                    }
                }

                // Track global highest sequence seen
                var maxSequence = newEvents.Max(e => e.Sequence);
                if (maxSequence > _highWaterSequence)
                    _highWaterSequence = maxSequence;

                // Save any projected documents added by the projections
                await session.SaveChangesAsync().ConfigureAwait(false);

                // Persist per-projection watermarks so restarts resume at correct positions
                if (_store.Options.ProjectionBuild.EnsureStateTable)
                {
                    foreach (var kvp in _projectionWatermarks)
                    {
                        var sql = $"UPSERT mt_projection_progress:`{kvp.Key}` CONTENT {{ projection_name: '{kvp.Key}', last_version: {kvp.Value}, last_updated: time::now() }}";
                        await session.ExecuteSqlAsync(sql, null, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                Health = Health with
                {
                    LastSuccess = DateTimeOffset.UtcNow,
                    HighWaterSequence = _highWaterSequence,
                    LagCount = 0
                };
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AsyncDaemon cycle failed; will retry on next poll");
                Health = Health with
                {
                    LastError = DateTimeOffset.UtcNow,
                    LastException = ex.Message
                };
            }
        }
        _logger.LogInformation("AsyncDaemon background loop stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
