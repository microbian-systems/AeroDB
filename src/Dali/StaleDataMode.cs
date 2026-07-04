namespace Dali;

/// <summary>
/// Controls staleness tolerance for <c>QueryForNonStaleData&lt;T&gt;</c>.
/// </summary>
public enum StaleDataMode
{
    /// <summary>
    /// Wait for the async daemon to catch up to the latest event sequence
    /// before returning results. Throws or times out if the timeout is exceeded.
    /// </summary>
    Strict,

    /// <summary>
    /// Return results immediately even if the async daemon has not yet
    /// processed all events. Results may be stale.
    /// </summary>
    AllowStale
}
