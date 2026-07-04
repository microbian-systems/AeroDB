namespace Dali;

/// <summary>
/// Options for configuring event subscriptions.
/// Controls batch size, polling interval, and stream-level filtering.
/// </summary>
public class SubscriptionOptions
{
    /// <summary>
    /// Maximum number of events to process in a single batch.
    /// Defaults to 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Polling interval in milliseconds for the async daemon.
    /// Defaults to 1000 ms.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Optional stream ID prefix filter. When set, only events from streams
    /// whose ID starts with this prefix trigger the subscription.
    /// </summary>
    public string? FilterByStreamPrefix { get; set; }
}
