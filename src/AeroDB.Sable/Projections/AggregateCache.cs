namespace AeroDB.Sable;

/// <summary>
/// LRU cache for projected aggregate documents.
/// Used by the async daemon to avoid reloading aggregates on every poll cycle.
/// Thread-safe for concurrent access.
/// </summary>
public class AggregateCache
{
    private readonly int _maxSize;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index;
    private readonly LinkedList<CacheEntry> _lruList;
    private readonly object _lock = new();

    public AggregateCache(int maxSize = 1000)
    {
        _maxSize = maxSize;
        _index = new Dictionary<string, LinkedListNode<CacheEntry>>(maxSize);
        _lruList = new LinkedList<CacheEntry>();
    }

    /// <summary>
    /// Try to get a cached aggregate by stream ID.
    /// </summary>
    public bool TryGetValue(string streamId, out object? aggregate)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(streamId, out var node))
            {
                // Move to front (most recently used)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                aggregate = node.Value.Aggregate;
                return true;
            }
            aggregate = null;
            return false;
        }
    }

    /// <summary>
    /// Cache or update an aggregate by stream ID.
    /// </summary>
    public void Set(string streamId, object aggregate)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(streamId, out var node))
            {
                node.Value = new CacheEntry(streamId, aggregate);
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return;
            }

            if (_index.Count >= _maxSize)
            {
                // Evict least recently used
                var last = _lruList.Last;
                if (last != null)
                {
                    _index.Remove(last.Value.StreamId);
                    _lruList.RemoveLast();
                }
            }

            var entry = new CacheEntry(streamId, aggregate);
            var newNode = _lruList.AddFirst(entry);
            _index[streamId] = newNode;
        }
    }

    /// <summary>Clear all cached entries.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
            _lruList.Clear();
        }
    }

    /// <summary>Current number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _index.Count;
        }
    }

    private sealed record CacheEntry(string StreamId, object Aggregate);
}
