using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.LiveQuery;

namespace Dali.LiveQuery;

internal sealed class SurrealDaliLiveQuery<T> : IDaliLiveQuery<T> where T : class
{
    private readonly Channel<DaliLiveChange<T>> _channel;
    private readonly SurrealDbLiveQuery<T>? _sdkLive;
    private readonly IAsyncEnumerable<SurrealDbLiveQueryResponse>? _testSource;
    private readonly IReadOnlyList<Action<T>>? _onCreated;
    private readonly IReadOnlyList<Action<T>>? _onUpdated;
    private readonly IReadOnlyList<Action<T>>? _onDeleted;
    private readonly IReadOnlyList<Action>? _onOpen;
    private readonly ILogger<SurrealDaliLiveQuery<T>> _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _readLoop;
    private Task? _callbackLoop;
    private int _changesConsumed; // 0 = available, 1 = consumed, guards against double-enumeration

    /// <summary>
    /// Production constructor: wraps a real <see cref="SurrealDbLiveQuery{T}"/> from the SDK.
    /// </summary>
    internal SurrealDaliLiveQuery(
        SurrealDbLiveQuery<T> sdkLive,
        IReadOnlyList<Action<T>>? onCreated,
        IReadOnlyList<Action<T>>? onUpdated,
        IReadOnlyList<Action<T>>? onDeleted,
        IReadOnlyList<Action>? onOpen,
        int channelCapacity,
        BoundedChannelFullMode fullMode,
        ILogger<SurrealDaliLiveQuery<T>> logger)
        : this(onCreated, onUpdated, onDeleted, onOpen, channelCapacity, fullMode, logger)
    {
        _sdkLive = sdkLive;
    }

    /// <summary>
    /// Test constructor: accepts a direct <see cref="IAsyncEnumerable{SurrealDbLiveQueryResponse}"/>
    /// so tests can feed controlled response sequences without needing a real SDK live query.
    /// </summary>
    internal SurrealDaliLiveQuery(
        IAsyncEnumerable<SurrealDbLiveQueryResponse> source,
        IReadOnlyList<Action<T>>? onCreated,
        IReadOnlyList<Action<T>>? onUpdated,
        IReadOnlyList<Action<T>>? onDeleted,
        IReadOnlyList<Action>? onOpen,
        int channelCapacity,
        BoundedChannelFullMode fullMode,
        ILogger<SurrealDaliLiveQuery<T>> logger)
        : this(onCreated, onUpdated, onDeleted, onOpen, channelCapacity, fullMode, logger)
    {
        _testSource = source;
    }

    private SurrealDaliLiveQuery(
        IReadOnlyList<Action<T>>? onCreated,
        IReadOnlyList<Action<T>>? onUpdated,
        IReadOnlyList<Action<T>>? onDeleted,
        IReadOnlyList<Action>? onOpen,
        int channelCapacity,
        BoundedChannelFullMode fullMode,
        ILogger<SurrealDaliLiveQuery<T>> logger)
    {
        _onCreated = onCreated;
        _onUpdated = onUpdated;
        _onDeleted = onDeleted;
        _onOpen = onOpen;
        _logger = logger;

        _channel = Channel.CreateBounded<DaliLiveChange<T>>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = fullMode,
                SingleWriter = true,
                SingleReader = false
            });
    }

    /// <summary>
    /// C1 FIXED: explicit start — caller observes Open event after this returns.
    /// Fires the SDK read loop and, if callbacks are registered, the callback dispatch loop.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _readLoop = Task.Run(() => ReadFromSdkAsync(ct));
        _logger.LogDebug("Live query read loop started for {Type}", typeof(T).Name);

        bool hasCallbacks = (_onCreated is { Count: > 0 })
                         || (_onUpdated is { Count: > 0 })
                         || (_onDeleted is { Count: > 0 })
                         || (_onOpen is { Count: > 0 });

        if (hasCallbacks)
        {
            _callbackLoop = Task.Run(() => DispatchCallbacksAsync());
            _logger.LogDebug("Live query callback loop started for {Type}", typeof(T).Name);
        }
    }

    /// <summary>
    /// Pulls <see cref="SurrealDbLiveQueryResponse"/> items from the SDK's <see cref="IAsyncEnumerable{T}"/>,
    /// maps them to <see cref="DaliLiveChange{T}"/>, and writes them into the bounded channel.
    /// On completion or failure, the channel writer is completed via <see cref="ChannelWriter{T}.TryComplete"/>.
    /// </summary>
    private async Task ReadFromSdkAsync(CancellationToken ct)
    {
        // Use test source if provided, otherwise read from the SDK live query
        var source = _testSource ?? _sdkLive!;

        try
        {
            await foreach (var response in source.WithCancellation(ct).ConfigureAwait(false))
            {
                var change = MapToDaliChange(response);
                await _channel.Writer.WriteAsync(change, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — expected when StopAsync or DisposeAsync cancels the CTS
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live query read loop failed for {Type}", typeof(T).Name);
        }
        finally
        {
            _channel.Writer.TryComplete();
            _logger.LogDebug("Live query read loop completed for {Type}", typeof(T).Name);
        }
    }

    /// <summary>
    /// C3 FIXED: each callback wrapped in try/catch via <see cref="InvokeSafe"/>.
    /// One bad handler does not kill the dispatch loop.
    /// Reads from the bounded channel and dispatches to the appropriate callback lists.
    /// </summary>
    private async Task DispatchCallbacksAsync()
    {
        await foreach (var change in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            switch (change.Action)
            {
                case DaliLiveAction.Open:
                    if (_onOpen is not null)
                    {
                        foreach (var handler in _onOpen)
                            InvokeOpenSafe(handler);
                    }
                    break;

                case DaliLiveAction.Created when change.Document is not null:
                    if (_onCreated is not null)
                    {
                        foreach (var handler in _onCreated)
                        {
                            InvokeSafe(handler, change.Document, "OnCreated");
                        }
                    }
                    break;

                case DaliLiveAction.Updated when change.Document is not null:
                    if (_onUpdated is not null)
                    {
                        foreach (var handler in _onUpdated)
                        {
                            InvokeSafe(handler, change.Document, "OnUpdated");
                        }
                    }
                    break;

                case DaliLiveAction.Deleted when change.Document is not null:
                    if (_onDeleted is not null)
                    {
                        foreach (var handler in _onDeleted)
                        {
                            InvokeSafe(handler, change.Document, "OnDeleted");
                        }
                    }
                    break;

                // Close events are silently consumed in callback mode
            }
        }
    }

    private void InvokeOpenSafe(Action handler)
    {
        try { handler(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnOpen handler threw");
        }
    }

    /// <summary>
    /// Invokes a single callback handler with a try/catch guard.
    /// C3 FIXED: per-handler isolation prevents one bad handler from breaking the loop.
    /// </summary>
    private void InvokeSafe(Action<T> handler, T document, string callbackName)
    {
        try
        {
            handler(document);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "{Callback} handler threw for document {Id}",
                callbackName,
                ExtractId(document));
        }
    }

    // ── IDaliLiveQuery<T> ─────────────────────────────────────────────────────────

    /// <summary>
    /// H4: Throws <see cref="InvalidOperationException"/> if callbacks are active
    /// (only one consumption model per subscription is allowed).
    /// L2: Guards against double-enumeration via <c>Interlocked.Exchange</c>.
    /// </summary>
    public IAsyncEnumerable<DaliLiveChange<T>> Changes(CancellationToken ct = default)
    {
        if (_callbackLoop is not null)
        {
            throw new InvalidOperationException(
                "Cannot call Changes() when callbacks are active. Use one consumption model per subscription.");
        }

        if (Interlocked.Exchange(ref _changesConsumed, 1) == 1)
        {
            throw new InvalidOperationException(
                "Changes() has already been enumerated. Create a new subscription.");
        }

        return _channel.Reader.ReadAllAsync(ct);
    }

    /// <summary>
    /// Exposes the underlying channel reader for custom pipelines.
    /// Returns <see langword="null"/> when callbacks are actively consuming the channel.
    /// </summary>
    public ChannelReader<DaliLiveChange<T>>? Reader
        => _callbackLoop is null ? _channel.Reader : null;

    /// <summary>
    /// C6 FIXED: Uses <see cref="ChannelWriter{T}.TryComplete"/> for idempotent completion.
    /// Cancels the internal CTS and awaits the read loop.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        if (_readLoop is not null)
        {
            await _readLoop.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes all resources: completes the channel, cancels the CTS,
    /// disposes the SDK live query (kills server-side query),
    /// and awaits both loops with exception swallowing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            // _sdkLive may be null in unit tests with mocked sessions
            if (_sdkLive is not null)
                await _sdkLive.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Swallow dispose exceptions
        }

        try
        {
            if (_readLoop is not null)
            {
                await _readLoop.ConfigureAwait(false);
            }
        }
        catch
        {
            // Swallow exceptions during disposal
        }

        try
        {
            if (_callbackLoop is not null)
            {
                await _callbackLoop.ConfigureAwait(false);
            }
        }
        catch
        {
            // Swallow exceptions during disposal
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Maps an SDK <see cref="SurrealDbLiveQueryResponse"/> to a Dali <see cref="DaliLiveChange{T}"/>.
    /// Pattern-matches on the concrete response type.
    /// </summary>
    private static DaliLiveChange<T> MapToDaliChange(SurrealDbLiveQueryResponse response)
        => response switch
        {
            SurrealDbLiveQueryOpenResponse
                => new DaliLiveChange<T>(DaliLiveAction.Open, null, default),

            SurrealDbLiveQueryCreateResponse<T> c
                => new DaliLiveChange<T>(DaliLiveAction.Created, ExtractId(c.Result), c.Result),

            SurrealDbLiveQueryUpdateResponse<T> u
                => new DaliLiveChange<T>(DaliLiveAction.Updated, ExtractId(u.Result), u.Result),

            SurrealDbLiveQueryDeleteResponse<T> d
                => new DaliLiveChange<T>(DaliLiveAction.Deleted, ExtractId(d.Result), d.Result),

            SurrealDbLiveQueryCloseResponse close
                => new DaliLiveChange<T>(DaliLiveAction.Closed, null, default, close.Reason),

            _ => throw new NotSupportedException(
                    $"Unknown SurrealDbLiveQueryResponse type: {response.GetType()}")
        };

    /// <summary>
    /// Extracts a string representation of the record ID from a document.
    /// Checks for <see cref="Record"/> base class first (fast path),
    /// then falls back to reflection on an "Id" property.
    /// </summary>
    private static string? ExtractId(T? doc)
    {
        if (doc is Record r)
        {
            return r.Id?.ToString();
        }

        // L1: Reflection fallback — consider replacing with source-generated accessor
        return doc?
            .GetType()
            .GetProperty("Id")
            ?.GetValue(doc)
            ?.ToString();
    }
}
