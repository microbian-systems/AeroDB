using BenchmarkDotNet.Attributes;
using System.Diagnostics;

namespace AeroDB.Benchmarks;

[SimpleJob(warmupCount: 2)]
[MemoryDiagnoser]
public class ThroughputBenchmarks
{
    private const int BatchSize = 1000;
    public static BenchDoc[] Docs = BenchDoc.Generate(BatchSize).ToArray();

    [GlobalSetup]
    public async Task Setup()
    {
        await BenchmarkStore.CleanAsync();
        // Pre-populate some data
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        foreach (var doc in BenchDoc.Generate(500))
            session.Store(doc);
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task<int> Insert_Throughput()
    {
        int count = 0;
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        foreach (var doc in Docs)
        {
            session.Store(doc);
            count++;
        }
        await session.SaveChangesAsync();
        return count;
    }

    [Benchmark]
    public async Task<double> Query_Latency_P50()
    {
        var sw = Stopwatch.StartNew();
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var results = await query.Query<BenchDoc>()
            .Where(d => d.Number > 500)
            .Take(50)
            .ToListAsync();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
