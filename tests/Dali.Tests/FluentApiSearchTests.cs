using System.Linq.Expressions;
using System.Reflection;
using Dahomey.Cbor.Attributes;
using SurrealDb.Net.Models;

namespace Dali.Tests;

// ─────────────────────────────────────────────────────
//  Test-only models
// ─────────────────────────────────────────────────────

internal sealed class TestProduct : IRecord
{
    [System.Text.Json.Serialization.JsonConverter(typeof(SurrealDb.Net.Json.ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Ean { get; set; } = "";
    public string Description { get; set; } = "";
    public float[] Embedding { get; set; } = [];
    public float[] Features { get; set; } = [];
    public decimal Price { get; set; }
}

internal sealed class SearchDoc : IRecord
{
    [System.Text.Json.Serialization.JsonConverter(typeof(SurrealDb.Net.Json.ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public float[] Embedding { get; set; } = [];
    public int Rank { get; set; }
    public bool Active { get; set; } = true;
}

// ─────────────────────────────────────────────────────
//  Tests
// ─────────────────────────────────────────────────────

public class FluentApiSearchTests
{
    // ─── Section 1: Schema API Tests (unit, no DB) ───

    [Test]
    public async Task FluentApi_FullSchema_WithAllMethods()
    {
        var options = new StoreOptions();
        options.Schema.For<TestProduct>()
            .Identity(x => x.Sku)
            .SetSchemaMode(SchemaMode.Strict)
            .BTreeIndex(x => x.Sku)
            .UniqueIndex(x => x.Ean)
            .FullTextIndex(x => x.Description, "english")
            .FullTextIndex("english", x => x.Name, x => x.Description)
            .HnswIndex(x => x.Embedding, 1536)
            .MtreeIndex(x => x.Features, 512, Search.Distance.Hamming);

        var mapping = (DocumentMapping<TestProduct>)options.Schema.Mappings[typeof(TestProduct)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
        mapping.IdentityProperty.ShouldBe("Sku");
        mapping.Indices.Count.ShouldBe(6);

        // BTreeIndex
        mapping.Indices[0].Name.ShouldBe("idx_test_product_sku");
        mapping.Indices[0].Type.ShouldBe(IndexType.Standard);
        mapping.Indices[0].Columns.ShouldBe(["Sku"]);

        // UniqueIndex
        mapping.Indices[1].Name.ShouldBe("uidx_test_product_ean");
        mapping.Indices[1].Type.ShouldBe(IndexType.Standard);
        mapping.Indices[1].IsUnique.ShouldBeTrue();
        mapping.Indices[1].Columns.ShouldBe(["Ean"]);

        // FullTextIndex (single field)
        mapping.Indices[2].Name.ShouldBe("ft_test_product_description");
        mapping.Indices[2].Type.ShouldBe(IndexType.FullText);
        mapping.Indices[2].Analyzer.ShouldBe("english");
        mapping.Indices[2].Columns.ShouldBe(["Description"]);

        // FullTextIndex (multi-field)
        mapping.Indices[3].Name.ShouldBe("ft_test_product_name_description");
        mapping.Indices[3].Type.ShouldBe(IndexType.FullText);
        mapping.Indices[3].Analyzer.ShouldBe("english");
        mapping.Indices[3].Columns.ShouldBe(["Name", "Description"]);

        // HnswIndex
        mapping.Indices[4].Name.ShouldBe("hnsw_test_product_embedding");
        mapping.Indices[4].Type.ShouldBe(IndexType.Hnsw);
        mapping.Indices[4].VectorDimension.ShouldBe(1536);
        mapping.Indices[4].VectorDistance.ShouldBe("COSINE");

        // MtreeIndex
        mapping.Indices[5].Name.ShouldBe("mtree_test_product_features");
        mapping.Indices[5].Type.ShouldBe(IndexType.Mtree);
        mapping.Indices[5].VectorDimension.ShouldBe(512);
        mapping.Indices[5].VectorDistance.ShouldBe("HAMMING");
    }

    [Test]
    public async Task FluentApi_BTreeIndex_IsAliasForIndex()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().BTreeIndex(p => p.Title);
        var idx = options.Schema.Mappings[typeof(SearchablePage)].Indices[0];
        idx.Type.ShouldBe(IndexType.Standard);
        idx.Columns.ShouldBe(["Title"]);
    }

    [Test]
    public async Task MtreeIndex_GeneratesCorrectSurql()
    {
        var idx = new IndexDefinition
        {
            Name = "mtree_page_features",
            Columns = ["Features"],
            Type = IndexType.Mtree,
            VectorDimension = 512,
            VectorDistance = Search.Distance.Hamming
        };
        var surql = BuildBuilderMethod("BuildMtreeIndex", idx, "page");
        surql.ShouldBe("DEFINE INDEX mtree_page_features ON TABLE page FIELDS Features MTREE DIMENSION 512 DIST HAMMING;");
    }

    [Test]
    public async Task FluentApi_BTreeIndex_WithCustomName()
    {
        var options = new StoreOptions();
        options.Schema.For<TestProduct>().Index(x => x.Name, idx => idx.WithName("custom_idx"));
        var mapping = options.Schema.Mappings[typeof(TestProduct)];
        mapping.Indices[0].Name.ShouldBe("custom_idx");
    }

    // ─── Section 2: Search Builder Integration Tests ───

    [Test]
    public async Task Search_Builder_MatchText_WithWhere_OrderBy_Skip_Take()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.For<SearchDoc>().FullTextIndex(x => x.Body, Search.Analyzer.Simple);
        });
        await using var session = await store.LightweightSessionAsync();

        // Store some test documents
        for (int i = 1; i <= 5; i++)
        {
            session.Store(new SearchDoc
            {
                Id = RecordId.From("search_doc", $"doc-{i}"),
                Title = $"Document {i}",
                Body = "laptop gaming desktop monitor",
                Rank = i,
                Active = i != 3 // doc-3 is inactive
            });
        }
        await session.SaveChangesAsync();

        // Search with chaining: FTS + filter + order + skip + take
        var results = await session.Search<SearchDoc>()
            .MatchText(x => (object)x.Body, "laptop")
            .Where(x => x.Active)
            .OrderBy(x => x.Rank)
            .Skip(1)
            .Take(2)
            .ToListAsync();

        results.Count.ShouldBe(2); // Excluding doc-3 (inactive), skipping doc-1, taking 2
        results.All(r => r.Active).ShouldBeTrue();
        results[0].Rank.ShouldBe(2); // doc-2
        results[1].Rank.ShouldBe(4); // doc-4
    }

    [Test]
    public async Task Search_Builder_MatchText_ReturnsTextResults()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.For<SearchDoc>().FullTextIndex(x => x.Body, Search.Analyzer.Simple);
        });
        await using var session = await store.LightweightSessionAsync();

        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "a"),
            Title = "Hello World",
            Body = "laptop review"
        });
        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "b"),
            Title = "Hello Mars",
            Body = "desktop review"
        });
        await session.SaveChangesAsync();

        var results = await session.Search<SearchDoc>()
            .MatchText(x => (object)x.Body, "laptop")
            .Take(10)
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Hello World");
    }

    [Test]
    public async Task Search_Builder_WithVector_ReturnsKnnResults()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.For<SearchDoc>().HnswIndex(x => x.Embedding, 3, Search.Distance.Cosine);
        });
        await using var session = await store.LightweightSessionAsync();
        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "v1"),
            Embedding = [1f, 0f, 0f]
        });
        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "v2"),
            Embedding = [0f, 1f, 0f]
        });
        await session.SaveChangesAsync();

        var queryVector = new float[] { 1f, 0.1f, 0f };
        var results = await session.Search<SearchDoc>()
            .WithVector(x => x.Embedding, queryVector)
            .Take(5)
            .ToListAsync();

        results.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Search_Builder_SearchExtension_ReturnsBuilder()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        var builder = session.Search<SearchDoc>();
        builder.ShouldNotBeNull();
        builder.ShouldBeAssignableTo<ISearchQuery<SearchDoc>>();
    }

    // ─── Private Helper ───

    /// <summary>
    /// Invokes a private static SurrealQL builder method on <see cref="SchemaManager"/> for unit testing.
    /// </summary>
    private static string BuildBuilderMethod(string methodName, IndexDefinition index, string tableName)
    {
        var method = typeof(SchemaManager).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) throw new InvalidOperationException($"Method {methodName} not found on SchemaManager");
        return (string)method.Invoke(null, [tableName, index])!;
    }
}
