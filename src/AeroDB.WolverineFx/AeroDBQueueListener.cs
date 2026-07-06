using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace AeroDB.WolverineFx;

/// <summary>
/// Polling listener that claims and dispatches incoming messages from SurrealDB.
/// Uses the AeroDBMessageStore to load globally-owned incoming envelopes,
/// then dispatches them to the provided IReceiver.
/// </summary>
public sealed class AeroDBQueueListener : IListener
{
    private readonly AeroDBMessageStore _store;
    private readonly IReceiver _receiver;
    private readonly AeroDBTransportOptions _options;
    private readonly ILogger<AeroDBQueueListener> _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _pollTask;
    private bool _started;

    public AeroDBQueueListener(
        AeroDBMessageStore store,
        IReceiver receiver,
        AeroDBTransportOptions options,
        ILogger<AeroDBQueueListener> logger,
        Uri address)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Address = address ?? throw new ArgumentNullException(nameof(address));
    }

    /// <summary>Pipeline from the receiver.</summary>
    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    /// <summary>The listener address.</summary>
    public Uri Address { get; }

    /// <summary>
    /// Start the polling loop.
    /// </summary>
    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        _pollTask = Task.Run(PollLoopAsync, _cancellation.Token);
        _logger.LogInformation("AeroDBQueueListener started for {Address}", Address);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Complete an envelope (no-op for this listener — the store handles persistence).
    /// </summary>
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    /// <summary>
    /// Defer an envelope back to the store for retry.
    /// </summary>
    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is null) return;
        // Re-insert as incoming with owner_id=0 so any node can pick it up
        try
        {
            var _ = envelope.Data; // Ensure serialized
            await _store.Inbox.StoreIncomingAsync(envelope);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to defer envelope {Id}", envelope.Id);
        }
    }

    /// <summary>
    /// Stop the listener.
    /// </summary>
    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch (OperationCanceledException) { }
        }
        _logger.LogInformation("AeroDBQueueListener stopped for {Address}", Address);
    }

    /// <summary>
    /// Dispose the listener.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cancellation.Dispose();
    }

    private async Task PollLoopAsync()
    {
        var failedCount = 0;
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var envelopes = await _store.LoadPageOfGloballyOwnedIncomingAsync(Address, _options.BatchSize);
                failedCount = 0;

                if (envelopes.Count > 0)
                {
                    // Atomically claim these messages for this node so no other node processes them
                    await _store.ReassignIncomingAsync(_store.GetOwnerId(), envelopes);

                    _logger.LogDebug("AeroDBQueueListener dispatching {Count} messages from {Address}",
                        envelopes.Count, Address);
                    await _receiver.ReceivedAsync(this, envelopes.ToArray());
                }
                else
                {
                    // No messages — sleep before next poll
                    await Task.Delay(_options.PollingInterval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failedCount++;
                var pause = failedCount > 5
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(failedCount * 200);
                _logger.LogError(ex,
                    "Error polling AeroDB queue at {Address} (failure #{FailedCount}), pausing {Pause}ms",
                    Address, failedCount, pause.TotalMilliseconds);
                try { await Task.Delay(pause, token); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
