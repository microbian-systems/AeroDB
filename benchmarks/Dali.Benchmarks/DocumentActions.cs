using BenchmarkDotNet.Attributes;
using SurrealDb.Net.Models;

namespace Dali.Benchmarks;

[SimpleJob(warmupCount: 2)]
[MemoryDiagnoser]
public class DocumentActions
{
    public static BenchDoc[] Docs = BenchDoc.Generate(100).ToArray();
    private BenchDoc[] _bulkDocs = Array.Empty<BenchDoc>();

    [GlobalSetup]
    public async Task Setup() => await BenchmarkStore.CleanAsync();

    [IterationSetup]
    public void IterationSetup()
    {
        // Regenerate docs each iteration so insert IDs are fresh
        Docs = BenchDoc.Generate(100).ToArray();
        _bulkDocs = BenchDoc.Generate(1000).ToArray();
    }

    [Benchmark]
    public async Task Record_Insert_100()
    {
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        foreach (var doc in Docs)
            session.Store(doc);
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task Record_Load_Single()
    {
        // Pre-insert one doc with explicit ID so we know it without relying on SaveChangesAsync back-propagation
        var docId = Guid.NewGuid().ToString();
        await using var setup = await BenchmarkStore.Store.LightweightSessionAsync();
        var doc = BenchDoc.Generate(1)[0];
        doc.Id = new RecordIdOf<string>("bench_doc", docId);
        setup.Store(doc);
        await setup.SaveChangesAsync();

        // Benchmark: load it
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var loaded = await query.LoadAsync<BenchDoc>(docId);
    }

    [Benchmark]
    public async Task Record_Query_By_Name()
    {
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var results = await query.Query<BenchDoc>()
            .Where(d => d.Name!.Contains("a"))
            .ToListAsync();
    }

    [Benchmark]
    public async Task Record_Bulk_Insert_1000()
    {
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        await session.BulkInsertAsync(_bulkDocs);
    }
}
