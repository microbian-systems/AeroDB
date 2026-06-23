using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Background worker that polls for new events and applies async projections.
/// Tracks a high-water mark to avoid re-processing events.
/// </summary>
public class AsyncDaemon : IAsyncDisposable
{
    private readonly IDocumentStore _store;
    private readonly IReadOnlyList<IProjection> _projections;
    private readonly ILogger<AsyncDaemon> _logger;
    private readonly object _lock = new();
    private Task? _runTask;
    private volatile bool _stopped;
    private long _highWaterSequence;

    /// <summary>
    /// Current health state of the daemon. Updated on each poll cycle.
    /// </summary>
    public DaemonHealthState Health { get; private set; } = new(false, null, null, 0, 0, null);

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

                await using var session = await _store.LightweightSessionAsync().ConfigureAwait(false);
                if (_stopped) break;

                // Fetch new events after the high-water sequence (global ordering)
                var newEvents = await session.Events.FetchAllAfterSequence(_highWaterSequence).ConfigureAwait(false);

                if (newEvents.Count == 0)
                    continue;

                _logger.LogDebug("AsyncDaemon: fetched {Count} new events after sequence {Sequence}",
                    newEvents.Count, _highWaterSequence);

                // Group events by stream for per-stream processing
                var streamGroups = newEvents
                    .GroupBy(e => e.StreamId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var projection in asyncProjections)
                {
                    foreach (var (streamId, events) in streamGroups)
                    {
                        var matchingEvents = events
                            .Where(e => e.Data is not null && projection.EventTypes.Contains(e.Data.GetType()))
                            .ToList();

                        if (matchingEvents.Count == 0)
                            continue;

                        _logger.LogInformation("Async projection {ProjectionType} applied on stream {StreamId}",
                            projection.GetType().Name, streamId);

                        var context = new ProjectionContext(session, matchingEvents.AsReadOnly());
                        await projection.ApplyAsync(context, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                // Track highest sequence seen (global ordering)
                var maxSequence = newEvents.Max(e => e.Sequence);
                if (maxSequence > _highWaterSequence)
                    _highWaterSequence = maxSequence;

                // Save any projected documents added by the projections
                await session.SaveChangesAsync().ConfigureAwait(false);

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
