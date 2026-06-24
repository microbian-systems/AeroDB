using BenchmarkDotNet.Attributes;

namespace Dali.Benchmarks;

[SimpleJob(warmupCount: 2)]
[MemoryDiagnoser]
public class StreamActions
{
    private const int RowCount = 500;

    [GlobalSetup]
    public async Task Setup()
    {
        await BenchmarkStore.CleanAsync();
        // Pre-populate 500 documents
        var docs = BenchDoc.Generate(RowCount);
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        foreach (var doc in docs)
            session.Store(doc);
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task<int> StreamAsync_All_Rows()
    {
        var count = 0;
        var sql = $"SELECT * FROM bench_doc ORDER BY Name LIMIT {RowCount}";
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var stream = query.AdvancedSql().StreamAsync<BenchDoc>(sql);
        await foreach (var doc in stream)
        {
            if (doc is not null) count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> QueryAsync_All_Rows()
    {
        var sql = $"SELECT * FROM bench_doc ORDER BY Name LIMIT {RowCount}";
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var results = await query.RawQueryAsync<BenchDoc>(sql);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> QueryAsync_LINQ_All_Rows()
    {
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var results = await query.Query<BenchDoc>()
            .OrderBy(d => d.Name!)
            .Take(RowCount)
            .ToListAsync();
        return results.Count;
    }

    [Benchmark]
    public async Task<int> StreamAsync_With_Parameters()
    {
        var count = 0;
        var sql = "SELECT * FROM bench_doc WHERE Number > $minNumber ORDER BY Name";
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var stream = query.AdvancedSql().StreamAsync<BenchDoc>(sql,
            new Dictionary<string, object?> { ["minNumber"] = 500 });
        await foreach (var doc in stream)
        {
            if (doc is not null) count++;
        }
        return count;
    }
}
