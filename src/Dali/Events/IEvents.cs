namespace Dali;

public interface IEvents
{
    Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default);
    Task Append(string streamId, IEnumerable<object> events, CancellationToken ct = default);
    Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default);
}
