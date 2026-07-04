using System.Collections.Concurrent;
using Dali.IdGeneration;
using TUnit.Core;

namespace Dali.Tests;

public class SnowflakeIdGeneratorTests
{
    // ─── Uniqueness ───────────────────────────────────────────

    [Test]
    public async Task NewId_GeneratesUniqueIds_OverLargeIteration()
    {
        var gen = new SnowflakeIdGenerator();
        var ids = new HashSet<long>();
        const int count = 10_000;

        for (var i = 0; i < count; i++)
        {
            var id = gen.NewId();
            ids.Add(id).ShouldBeTrue($"Collision at iteration {i}: id={id}");
        }

        ids.Count.ShouldBe(count);
    }

    // ─── Positivity ───────────────────────────────────────────

    [Test]
    public async Task NewId_ReturnsPositiveValues()
    {
        var gen = new SnowflakeIdGenerator();
        const int count = 10_000;

        for (var i = 0; i < count; i++)
        {
            var id = gen.NewId();
            id.ShouldBeGreaterThan(0, $"Expected positive id, got {id} at iteration {i}");
        }
    }

    // ─── Monotonic Ordering ───────────────────────────────────

    [Test]
    public async Task NewId_IsMonotonicallyIncreasing()
    {
        var gen = new SnowflakeIdGenerator();
        const int count = 10_000;
        var previous = gen.NewId();

        for (var i = 1; i < count; i++)
        {
            var current = gen.NewId();
            current.ShouldBeGreaterThan(previous,
                $"Expected id to increase, got {current} after {previous} at iteration {i}");
            previous = current;
        }
    }

    // ─── Timestamp Component ──────────────────────────────────

    [Test]
    public async Task NewId_IdsContainTimestampComponent()
    {
        var gen = new SnowflakeIdGenerator();

        // Generate an ID now and one after a small delay.
        // Snowflake IDs embed a millisecond timestamp in the high bits,
        // so the high bits should differ if the timestamp differs.
        var id1 = gen.NewId();
        await Task.Delay(10);
        var id2 = gen.NewId();
        await Task.Delay(10);
        var id3 = gen.NewId();

        // The top ~41 bits contain the timestamp offset.
        // Shift right by the worker+sequence bits (at least 12 bits in standard snowflakes).
        // Different timestamps yield different high bits.
        var high1 = id1 >> 12;
        var high2 = id2 >> 12;
        var high3 = id3 >> 12;

        // At least one of the pairs should have a different timestamp portion.
        (high1 != high2 || high2 != high3)
            .ShouldBeTrue("Expected at least one pair of IDs to have a different timestamp component");
    }

    [Test]
    public async Task NewId_IdsGeneratedInSameMillisecond_AreDistinct()
    {
        var gen = new SnowflakeIdGenerator();
        var ids = new HashSet<long>();

        // Generate many IDs rapidly (likely within the same ms).
        for (var i = 0; i < 100; i++)
        {
            ids.Add(gen.NewId()).ShouldBeTrue($"Collision during rapid generation at index {i}");
        }

        ids.Count.ShouldBe(100);
    }

    // ─── Static Configuration — Machine ID ────────────────────

    [Test]
    public async Task Constructor_WithMachineId_Default_DoesNotThrow()
    {
        // Default constructor uses random machine ID (1-1023).
        var gen = new SnowflakeIdGenerator();
        var id = gen.NewId();
        id.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Constructor_WithMachineId_GeneratesIdsAtBoundaries()
    {
        // Machine ID 0 — lowest valid value.
        var gen0 = new SnowflakeIdGenerator(0);
        var id0 = gen0.NewId();
        id0.ShouldBeGreaterThan(0);

        // Machine ID 1023 — highest valid value.
        var gen1023 = new SnowflakeIdGenerator(1023);
        var id1023 = gen1023.NewId();
        id1023.ShouldBeGreaterThan(0);

        // Machine ID 1 — common minimum real value.
        var gen1 = new SnowflakeIdGenerator(1);
        var id1 = gen1.NewId();
        id1.ShouldBeGreaterThan(0);
    }

    // ─── Different Instances — Same Behaviour ─────────────────

    [Test]
    public async Task MultipleInstances_ProduceUniqueIds()
    {
        var gen1 = new SnowflakeIdGenerator();
        var gen2 = new SnowflakeIdGenerator();
        var ids = new HashSet<long>();

        for (var i = 0; i < 1_000; i++)
        {
            ids.Add(gen1.NewId()).ShouldBeTrue();
            ids.Add(gen2.NewId()).ShouldBeTrue();
        }

        ids.Count.ShouldBe(2_000);
    }

    // ─── Thread Safety ────────────────────────────────────────

    [Test]
    public async Task ThreadSafety_MultipleThreads_NoCollisions()
    {
        const int threads = 8;
        const int idsPerThread = 2_000;
        var gen = new SnowflakeIdGenerator();
        var allIds = new ConcurrentBag<long>();

        var tasks = Enumerable.Range(0, threads).Select(_ =>
            Task.Run(() =>
            {
                for (var i = 0; i < idsPerThread; i++)
                {
                    allIds.Add(gen.NewId());
                }
            }));

        await Task.WhenAll(tasks);

        var distinct = allIds.Distinct().Count();
        distinct.ShouldBe(threads * idsPerThread,
            $"Expected {threads * idsPerThread} unique IDs, got {distinct}");
    }

    [Test]
    public async Task ThreadSafety_MultipleInstances_MultipleThreads_NoCollisions()
    {
        const int threads = 8;
        const int idsPerThread = 2_000;
        var allIds = new ConcurrentBag<long>();

        var tasks = Enumerable.Range(0, threads).Select(i =>
            Task.Run(() =>
            {
                // Each thread gets its own generator with a different machine ID.
                var gen = new SnowflakeIdGenerator(i % 1024);
                for (var j = 0; j < idsPerThread; j++)
                {
                    allIds.Add(gen.NewId());
                }
            }));

        await Task.WhenAll(tasks);

        var distinct = allIds.Distinct().Count();
        distinct.ShouldBe(threads * idsPerThread,
            $"Expected {threads * idsPerThread} unique IDs, got {distinct}");
    }

}
