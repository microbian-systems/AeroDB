namespace AeroDB;

/// <summary>
/// Health state snapshot of the <see cref="AsyncDaemon"/>.
/// </summary>
/// <param name="IsRunning">Whether the daemon is currently running.</param>
/// <param name="LastSuccess">Timestamp of the last successful poll cycle.</param>
/// <param name="LastError">Timestamp of the last failed poll cycle.</param>
/// <param name="HighWaterSequence">The global sequence number of the latest processed event.</param>
/// <param name="LagCount">Number of unprocessed events behind the current sequence.</param>
/// <param name="LastException">The exception from the last failed cycle, if any.</param>
public sealed record DaemonHealthState(
    bool IsRunning,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastError,
    long HighWaterSequence,
    int LagCount,
    string? LastException
);
