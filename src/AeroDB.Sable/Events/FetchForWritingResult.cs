namespace AeroDB.Sable;

/// <summary>
/// Internal interface used by <see cref="DocumentSession"/> to flush
/// <see cref="FetchForWritingResult{T}"/> pending events during SaveChangesAsync.
/// </summary>
internal interface IFetchForWritingResult
{
    string StreamId { get; }
    long ExpectedVersion { get; }
    IReadOnlyList<object> PendingEvents { get; }
}

/// <summary>
/// Wraps a fetched aggregate for write-model operations. Tracks the expected
/// stream version so SaveChangesAsync can detect concurrency conflicts.
/// </summary>
public class FetchForWritingResult<T> : IFetchForWritingResult where T : class
{
    private readonly List<object> _pending = new();
    private readonly T? _aggregate;

    internal FetchForWritingResult(T? aggregate, long expectedVersion, string streamId)
    {
        _aggregate = aggregate;
        ExpectedVersion = expectedVersion;
        StreamId = streamId;
    }

    /// <summary>The aggregate instance, or null if the stream has no events.</summary>
    public T Aggregate => _aggregate!;

    /// <summary>The version that was current when the aggregate was fetched.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The stream identifier.</summary>
    string IFetchForWritingResult.StreamId => StreamId;
    internal string StreamId { get; }

    /// <summary>Events queued for append during SaveChangesAsync.</summary>
    IReadOnlyList<object> IFetchForWritingResult.PendingEvents => _pending;
    internal IReadOnlyList<object> PendingEvents => _pending;

    /// <summary>Queue one event for append. The version check happens in SaveChangesAsync.</summary>
    public void AppendOne(object @event)
    {
        _pending.Add(@event);
    }

    /// <summary>Queue multiple events for append.</summary>
    public void AppendMany(IEnumerable<object> events)
    {
        _pending.AddRange(events);
    }
}
