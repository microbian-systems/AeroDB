namespace WolverineFx.Dali;

/// <summary>
/// Configuration options for the Dali-backed transport.
/// Controls polling behavior and batch sizes for the SurrealDB message queue.
/// </summary>
public sealed class DaliTransportOptions
{
    /// <summary>
    /// How often to poll for new incoming messages when the queue is idle.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of messages to pull from the database in a single polling batch.
    /// Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// When true, uses SurrealDB's LIVE SELECT to push notifications instead of polling.
    /// Not yet implemented — reserved for future use.
    /// </summary>
    public bool UseLiveQuery { get; set; }
}
