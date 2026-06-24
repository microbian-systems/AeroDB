using BenchmarkDotNet.Attributes;

namespace Dali.Benchmarks;

[SimpleJob(warmupCount: 2)]
[MemoryDiagnoser]
public class EventActions
{
    public static BenchEvent[] Events = Enumerable.Range(0, 1000)
        .Select(i => new BenchEvent($"Event_{i}"))
        .ToArray();

    [GlobalSetup]
    public async Task Setup()
    {
        await BenchmarkStore.CleanAsync();
        await BenchmarkStore.CleanEventsAsync();
    }

    [Benchmark]
    public async Task Append_1000_Events()
    {
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        var streamId = Guid.NewGuid().ToString("D");
        await session.Events.StartStream(streamId, Events.Cast<object>().ToArray());
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task Aggregate_Stream()
    {
        // Setup: append 100 events first (not measured)
        var streamId = Guid.NewGuid().ToString("D");
        await using (var setup = await BenchmarkStore.Store.LightweightSessionAsync())
        {
            await setup.Events.StartStream(streamId, Events.Take(100).Cast<object>().ToArray());
            await setup.SaveChangesAsync();
        }

        // Benchmark: aggregate replay via live stream aggregation
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        var aggregate = await session.Events.AggregateAsync<BenchAggregate>(streamId);
    }
}

public record BenchEvent(string Name);

public class BenchAggregate
{
    public List<string> EventNames { get; set; } = new();

    public void Apply(BenchEvent e) => EventNames.Add(e.Name);
}
