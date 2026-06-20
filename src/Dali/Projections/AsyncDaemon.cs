namespace Dali;

/// <summary>
/// Background worker that polls for new events and applies async projections.
/// Tracks a high-water mark to avoid re-processing events.
/// </summary>
public class AsyncDaemon : IAsyncDisposable
{
    private readonly IDocumentStore _store;
    private readonly IReadOnlyList<IProjection> _projections;
    private readonly object _lock = new();
    private Task? _runTask;
    private volatile bool _stopped;
    private long _highWaterMark;

    public AsyncDaemon(IDocumentStore store, IReadOnlyList<IProjection> projections)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
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
            _runTask = RunAsync(pollInterval);
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
            return;

        try
        {
            await runTask;
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
    }

    private async Task RunAsync(TimeSpan pollInterval)
    {
        while (!_stopped)
        {
            try
            {
                await Task.Delay(pollInterval);
                if (_stopped) break;

                var asyncProjections = _projections
                    .Where(p => p.Lifecycle == ProjectionLifecycle.Async)
                    .ToList();

                if (asyncProjections.Count == 0)
                    continue;

                await using var session = await _store.LightweightSessionAsync();
                if (_stopped) break;

                // Fetch new events after the high-water mark
                var newEvents = await session.Events.FetchAllAfterVersion(_highWaterMark);

                if (newEvents.Count == 0)
                    continue;

                // Group events by stream for per-stream processing
                var streamGroups = newEvents
                    .GroupBy(e => e.StreamId)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Event).ToList());

                foreach (var projection in asyncProjections)
                {
                    foreach (var (streamId, events) in streamGroups)
                    {
                        var matchingEvents = events
                            .Where(e => projection.EventTypes.Contains(e.GetType()))
                            .ToList();

                        if (matchingEvents.Count == 0)
                            continue;

                        var context = new ProjectionContext(session, matchingEvents.AsReadOnly());
                        await projection.ApplyAsync(context, CancellationToken.None);
                    }
                }

                // Track highest version seen
                var maxVersion = newEvents.Max(e => e.Version);
                if (maxVersion > _highWaterMark)
                    _highWaterMark = maxVersion;

                // Save any projected documents added by the projections
                await session.SaveChangesAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Log and continue (logging TBD)
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
