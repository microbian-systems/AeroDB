using Dali;
using SurrealDb.Embedded.InMemory;

namespace Dali.Benchmarks;

public static class BenchmarkStore
{
    public static IDocumentStore Store { get; }

    static BenchmarkStore()
    {
        Store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.Namespace = "benchmark";
            o.Database = "benchmark";
            o.Schema.For<BenchDoc>().SetSchemaMode(SchemaMode.Flexible).Index(d => d.Name);
        });
        Store.InitializeAsync().GetAwaiter().GetResult();
    }

    public static async Task CleanAsync()
    {
        await using var session = await Store.LightweightSessionAsync();
        await session.RawQueryAsync<object>("DELETE bench_doc;");
    }

    public static async Task CleanEntityDocsAsync()
    {
        try
        {
            await using var session = await Store.LightweightSessionAsync();
            await session.RawQueryAsync<object>("DELETE entity_doc;");
        }
        catch (Exception)
        {
            // Table may not exist yet. Entity types create tables implicitly
            // on first UPSERT, so there's no data to clean.
        }
    }

    public static async Task CleanEventsAsync()
    {
        await using var session = await Store.LightweightSessionAsync();
        await session.RawQueryAsync<object>("DELETE mt_events;");
    }
}
