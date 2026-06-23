namespace Dali;

/// <summary>
/// Controls how events are appended to the event store.
/// </summary>
public enum EventAppendMode
{
    /// <summary>
    /// Rich mode (default). Full metadata tracking: version, sequence, timestamp, stream key, correlation.
    /// </summary>
    Rich = 0,
    
    /// <summary>
    /// Quick mode. Minimal overhead — stores only event type and data.
    /// Sequence is not generated, correlation is not auto-populated.
    /// </summary>
    Quick = 1
}
