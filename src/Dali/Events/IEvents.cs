namespace Dali;

public interface IEvents
{
    Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default);
    Task Append(string streamId, IEnumerable<object> events, CancellationToken ct = default);
    Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Fetches all events after a given version (for async projections / polling).
    /// Returns tuples of (streamId, event, version).
    /// </summary>
    Task<IReadOnlyList<(string StreamId, object Event, long Version)>> FetchAllAfterVersion(
        long version, CancellationToken ct = default);
}
