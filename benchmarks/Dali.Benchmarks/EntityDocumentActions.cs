using BenchmarkDotNet.Attributes;

namespace Dali.Benchmarks;

[SimpleJob(warmupCount: 2)]
[MemoryDiagnoser]
public class EntityDocumentActions
{
    public static EntityDoc[] Docs = EntityDoc.Generate(100).ToArray();
    private EntityDoc[] _bulkDocs = Array.Empty<EntityDoc>();

    [GlobalSetup]
    public async Task Setup() => await BenchmarkStore.CleanEntityDocsAsync();

    [IterationSetup]
    public void IterationSetup()
    {
        // Regenerate docs each iteration so insert IDs are fresh
        Docs = EntityDoc.Generate(100).ToArray();
        _bulkDocs = EntityDoc.Generate(1000).ToArray();
    }

    [Benchmark]
    public async Task Entity_Insert_100()
    {
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        foreach (var doc in Docs)
            session.Store(doc);
        await session.SaveChangesAsync();
    }

    [Benchmark]
    public async Task Entity_Load_Single()
    {
        // Pre-insert one doc with explicit ID so we know it without relying on SaveChangesAsync back-propagation
        var docId = SnowflakeGenerator.NewId();
        await using var setup = await BenchmarkStore.Store.LightweightSessionAsync();
        var doc = EntityDoc.Generate(1)[0];
        doc.Id = docId;
        setup.Store(doc);
        await setup.SaveChangesAsync();

        // Benchmark: load it
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var loaded = await query.LoadAsync<EntityDoc>(docId.ToString());
    }

    [Benchmark]
    public async Task Entity_Query_By_Name()
    {
        await using var query = await BenchmarkStore.Store.QuerySessionAsync();
        var results = await query.Query<EntityDoc>()
            .Where(d => d.Name!.Contains("a"))
            .ToListAsync();
    }

    [Benchmark]
    public async Task Entity_Bulk_Insert_1000()
    {
        await using var session = await BenchmarkStore.Store.LightweightSessionAsync();
        await session.BulkInsertAsync(_bulkDocs);
    }
}
