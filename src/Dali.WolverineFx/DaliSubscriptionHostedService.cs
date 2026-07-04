using Dali;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
// Resolve ambiguity: JasperFx.Events.IEvent is the primary IEvent used in this file
using IEvent = JasperFx.Events.IEvent;

namespace Dali.WolverineFx;

/// <summary>
/// Background service that polls the Dali event store and dispatches new events
/// to registered subscriptions via <see cref="DaliSubscriptionRunner"/>.
/// </summary>
internal class DaliSubscriptionHostedService : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DaliSubscriptionHostedService> _logger;
    private readonly List<SubscriptionState> _states = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _disposed;

    public DaliSubscriptionHostedService(
        IServiceProvider services,
        IEnumerable<DaliSubscriptionRunner> runners,
        ILogger<DaliSubscriptionHostedService>? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger<DaliSubscriptionHostedService>.Instance;

        foreach (var runner in runners)
        {
            _states.Add(new SubscriptionState(runner, runner.Options));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_states.Count == 0)
        {
            _logger.LogInformation("No Dali subscriptions registered; daemon not started");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("Dali Subscription Daemon started with {Count} subscriptions", _states.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        _cts.Dispose();

        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping subscription daemon"); }
        }

        _logger.LogInformation("Dali Subscription Daemon stopped");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct);
                if (ct.IsCancellationRequested) break;

                var store = _services.GetRequiredService<IDocumentStore>();
                await using var pollSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct);

                // Fetch all new events since the highest known sequence across all subscriptions
                var maxWatermark = _states.Count > 0
                    ? _states.Max(s => s.HighWaterSequence)
                    : 0;

                var rawEvents = await pollSession.Events.FetchAllAfterSequence(maxWatermark, ct);

                if (rawEvents.Count == 0)
                    continue;

                _logger.LogDebug("Subscription daemon: fetched {Count} new events after seq {Sequence}",
                    rawEvents.Count, maxWatermark);

                // Build an EventRange from the raw events
                var range = BuildEventRange(rawEvents, maxWatermark);

                // Dispatch to each subscription
                foreach (var state in _states)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var controller = new DaliSubscriptionController(_logger);
                        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct);
                        await state.Runner.ProcessBatchAsync(range, controller, session, ct);
                        // Track global sequence (not version) for polling semantics
                        state.HighWaterSequence = Math.Max(state.HighWaterSequence,
                            rawEvents.Max(e => e.Sequence));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Subscription {Name} failed processing batch v{From}-v{To}",
                            state.Runner.SubscriptionName, range.SequenceFloor, range.SequenceCeiling);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subscription daemon poll cycle failed; will retry");
            }
        }
    }

    private static EventRange BuildEventRange(
        IReadOnlyList<global::Dali.IEvent> rawEvents,
        long baseVersion)
    {
        var events = rawEvents
            .Select(e => new DaliEnvelopeEvent(e.Data, e.StreamId, e.Version) as JasperFx.Events.IEvent)
            .ToList();

        var minVersion = rawEvents.Count > 0 ? rawEvents.Min(e => e.Version) : baseVersion + 1;
        var maxVersion = rawEvents.Count > 0 ? rawEvents.Max(e => e.Version) : baseVersion;

        var shardName = new ShardName("dali-subscriptions");

        var range = new EventRange(shardName, minVersion, maxVersion, agent: null!);
        range.Events = events;
        return range;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Dispose();
        if (_runTask is not null)
        {
            try { await _runTask; } catch { }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SubscriptionState
    {
        public DaliSubscriptionRunner Runner { get; }
        public AsyncOptions Options { get; }
        public long HighWaterSequence { get; set; }

        public SubscriptionState(DaliSubscriptionRunner runner, AsyncOptions options)
        {
            Runner = runner;
            Options = options;
        }
    }

    /// <summary>
    /// Simple in-process implementation of ISubscriptionController for the daemon.
    /// Logs dead letter events and critical failures.
    /// </summary>
    private sealed class DaliSubscriptionController : ISubscriptionController
    {
        private readonly ILogger _logger;

        public DaliSubscriptionController(ILogger logger) => _logger = logger;

        public ErrorHandlingOptions ErrorOptions { get; set; } = new();
        public ShardName Name { get; } = new("dali-daemon");
        public ShardExecutionMode Mode => ShardExecutionMode.Continuous;
        public AsyncOptions Options { get; } = new();

        public ValueTask MarkSuccessAsync(long sequence)
        {
            _logger.LogDebug("Subscription controller marked success at position {Sequence}", sequence);
            return ValueTask.CompletedTask;
        }

        public Task RecordDeadLetterEventAsync(JasperFx.Events.IEvent evt, Exception ex)
        {
            _logger.LogWarning(ex, "Dead letter event: {EventType} v{Version} in stream {StreamId}",
                evt.EventTypeName, evt.Version, evt.StreamId);
            return Task.CompletedTask;
        }

        public Task ReportCriticalFailureAsync(Exception ex)
        {
            _logger.LogError(ex, "Subscription critical failure");
            return Task.CompletedTask;
        }

        public Task ReportCriticalFailureAsync(Exception ex, long position)
        {
            _logger.LogError(ex, "Subscription critical failure at position {Position}", position);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Minimal IEvent implementation for wrapping raw Dali events into JasperFx EventRange.
/// </summary>
internal sealed class DaliEnvelopeEvent : JasperFx.Events.IEvent
{
    private readonly Dictionary<string, object?> _headers = new();
    private List<EventTag>? _tags;

    public DaliEnvelopeEvent(object data, string streamId, long version)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        StreamId = Guid.TryParse(streamId, out var g) ? g : Guid.Empty;
        Version = version;
        Sequence = version;
        EventType = data.GetType();
        EventTypeName = data.GetType().Name;
    }

    // ─── IEvent properties ───

    public Guid Id { get; set; } = Guid.Empty;
    public long Version { get; set; }
    public long Sequence { get; set; }
    public object Data { get; }
    public Guid StreamId { get; set; }
    public string? StreamKey { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string TenantId { get; set; } = "";
    public Type EventType { get; }
    public string EventTypeName { get; set; }
    public string DotNetTypeName { get; set; } = "";
    public string? CausationId { get; set; }
    public string? CorrelationId { get; set; }
    public Dictionary<string, object>? Headers { get; set; } = new();
    IReadOnlyList<EventTag>? JasperFx.Events.IEvent.Tags => _tags?.AsReadOnly();
    public bool IsArchived { get; set; }
    public string? AggregateTypeName { get; set; }
    public string? UserName { get; set; }
    public bool IsSkipped { get; set; }

    // ─── IEvent methods ───

    public void SetHeader(string key, object value)
    {
        _headers[key] = value;
    }

    public object? GetHeader(string key)
    {
        return _headers.TryGetValue(key, out var value) ? value : null;
    }

    public Func<JasperFx.Events.IEvent, T> CreateAggregateIdentitySource<T>()
    {
        throw new NotSupportedException("Dali envelope events do not support aggregate identity resolution");
    }

    void JasperFx.Events.IEvent.AddTag<T>(T tag)
    {
        _tags ??= new List<EventTag>();
        _tags.Add(new EventTag(typeof(T), tag!));
    }

    public void AddTag(EventTag tag)
    {
        _tags ??= new List<EventTag>();
        _tags.Add(tag);
    }
}
