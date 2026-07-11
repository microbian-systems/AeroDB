using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

// ─── Test document type for cache tests ─────────────────────────────

internal sealed class CacheDoc
{
    public string Name { get; set; } = "";
}

// ─── Tests ──────────────────────────────────────────────────────────

public class AggregateCacheTests
{
    [Test]
    public void Cache_set_and_get()
    {
        var cache = new AggregateCache(10);
        var obj = new CacheDoc { Name = "cached" };
        cache.Set("stream-1", obj);
        cache.TryGetValue("stream-1", out var result).ShouldBeTrue();
        result.ShouldBe(obj);
    }

    [Test]
    public void Cache_evicts_when_full()
    {
        var cache = new AggregateCache(2);
        cache.Set("a", new CacheDoc());
        cache.Set("b", new CacheDoc());
        cache.Set("c", new CacheDoc()); // should evict "a"
        cache.TryGetValue("a", out _).ShouldBeFalse();
        cache.TryGetValue("b", out _).ShouldBeTrue();
        cache.TryGetValue("c", out _).ShouldBeTrue();
    }

    [Test]
    public void Cache_count_tracks_entries()
    {
        var cache = new AggregateCache(10);
        cache.Count.ShouldBe(0);
        cache.Set("a", new CacheDoc());
        cache.Count.ShouldBe(1);
        cache.Set("b", new CacheDoc());
        cache.Count.ShouldBe(2);
    }

    [Test]
    public void Cache_update_moves_to_front()
    {
        var cache = new AggregateCache(3);
        cache.Set("a", new CacheDoc { Name = "a" });
        cache.Set("b", new CacheDoc { Name = "b" });
        cache.Set("c", new CacheDoc { Name = "c" });

        // Update "a" — should move to front
        cache.Set("a", new CacheDoc { Name = "a-updated" });

        // Now add two more to fill cache and trigger eviction of LRU
        cache.Set("d", new CacheDoc { Name = "d" });
        cache.Set("e", new CacheDoc { Name = "e" });

        // "a" was most recently updated, so it should survive
        cache.TryGetValue("a", out var result).ShouldBeTrue();
        var doc = result.ShouldBeOfType<CacheDoc>();
        doc.Name.ShouldBe("a-updated");

        // "b" was LRU and should be evicted
        cache.TryGetValue("b", out _).ShouldBeFalse();
    }

    [Test]
    public void Clear_removes_all_entries()
    {
        var cache = new AggregateCache(10);
        cache.Set("a", new CacheDoc());
        cache.Set("b", new CacheDoc());
        cache.Count.ShouldBe(2);
        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.TryGetValue("a", out _).ShouldBeFalse();
        cache.TryGetValue("b", out _).ShouldBeFalse();
    }

    [Test]
    public void TryGetValue_returns_false_for_missing_key()
    {
        var cache = new AggregateCache(10);
        cache.TryGetValue("nonexistent", out var result).ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Test]
    public void Constructor_default_maxSize_is_1000()
    {
        var cache = new AggregateCache();
        cache.Count.ShouldBe(0);
    }
}
