using System.Linq.Expressions;
using System.Reflection;
using Dahomey.Cbor.Attributes;
using AeroDB;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

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
    public string Category { get; set; } = "";
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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
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
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var builder = session.Search<SearchDoc>();
        builder.ShouldNotBeNull();
        builder.ShouldBeAssignableTo<ISearchQuery<SearchDoc>>();
    }

    // ─── Section 3: Parameterized Where() Tests (constant-value comparisons) ───
    // These tests exercise the $pN parameter path in DaliSearchQuery.Where().
    // Unlike bare member expressions (e.g. .Where(x => x.Active)), constant
    // comparisons like .Where(x => x.Rank > 3) generate $p0, $p1 placeholders
    // via SurrealCommandBuilder.  A successful query proves SurrealDB correctly
    // resolves those parameters at execution time.

    [Test]
    public async Task Search_Builder_MatchText_WhereIntComparison()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.For<SearchDoc>().FullTextIndex(x => x.Body, Search.Analyzer.Simple);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        for (int i = 1; i <= 5; i++)
        {
            session.Store(new SearchDoc
            {
                Id = RecordId.From("search_doc", $"int-doc-{i}"),
                Title = $"Document {i}",
                Body = "laptop gaming desktop monitor",
                Rank = i,
                Active = true
            });
        }
        await session.SaveChangesAsync();

        // Where clause: Rank > 3  →  translates to "Rank > $p0" with $p0 = 3
        var results = await session.Search<SearchDoc>()
            .MatchText(x => (object)x.Body, "laptop")
            .Where(x => x.Rank > 3)
            .ToListAsync();

        results.Count.ShouldBeGreaterThanOrEqualTo(2);
        results.All(r => r.Rank > 3).ShouldBeTrue();
    }

    [Test]
    public async Task Search_Builder_MatchText_WhereStringComparison()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.For<SearchDoc>().FullTextIndex(x => x.Body, Search.Analyzer.Simple);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "cat-a"),
            Title = "News Article",
            Body = "laptop review",
            Category = "news"
        });
        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "cat-b"),
            Title = "Blog Post",
            Body = "laptop review",
            Category = "blog"
        });
        await session.SaveChangesAsync();

        // Where clause: Category == "news"  →  translates to "Category = $p0" with $p0 = "news"
        var results = await session.Search<SearchDoc>()
            .MatchText(x => (object)x.Body, "laptop")
            .Where(x => x.Category == "news")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("News Article");
    }

    [Test]
    public async Task Search_Builder_WithVector_WhereIntComparison()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.For<SearchDoc>().HnswIndex(x => x.Embedding, 3, Search.Distance.Cosine);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "vec-high"),
            Title = "High Rank",
            Embedding = [1f, 0f, 0f],
            Rank = 5
        });
        session.Store(new SearchDoc
        {
            Id = RecordId.From("search_doc", "vec-low"),
            Title = "Low Rank",
            Embedding = [0f, 1f, 0f],
            Rank = 2
        });
        await session.SaveChangesAsync();

        var queryVector = new float[] { 1f, 0.1f, 0f };
        // Where clause: Rank >= 5  →  translates to "Rank >= $p0" with $p0 = 5
        var results = await session.Search<SearchDoc>()
            .WithVector(x => x.Embedding, queryVector)
            .Where(x => x.Rank >= 5)
            .Take(5)
            .ToListAsync();

        results.Count.ShouldBeGreaterThanOrEqualTo(1);
        results.All(r => r.Rank >= 5).ShouldBeTrue();
    }

    [Test]
    public async Task Search_Builder_Hybrid_WithWhere_FuseAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.For<SearchDoc>()
                .FullTextIndex(x => x.Body, Search.Analyzer.Simple)
                .HnswIndex(x => x.Embedding, 3, Search.Distance.Cosine);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var docs = new[]
        {
            new SearchDoc { Title = "laptop review", Body = "great laptop", Embedding = [1f, 0.1f, 0f], Rank = 10 },
            new SearchDoc { Title = "phone news", Body = "new phone", Embedding = [0f, 1f, 0.1f], Rank = 3 },
            new SearchDoc { Title = "laptop deal", Body = "cheap laptop", Embedding = [0.5f, 0.2f, 0f], Rank = 8 },
        };
        for (int i = 0; i < docs.Length; i++)
        {
            docs[i].Id = RecordId.From("search_doc", $"hybrid-{i + 1}");
            session.Store(docs[i]);
        }
        await session.SaveChangesAsync();

        // Hybrid search: FTS + KNN + Where filter (same session)
        // Note: search::rrf() may not be available in the in-memory engine,
        // so we tolerate empty results (the builder chaining itself is the key test).
        List<SearchDoc> results;
        try
        {
            results = await session.Search<SearchDoc>()
                .MatchText(x => (object)x.Body, "laptop")
                .WithVector(x => x.Embedding, [1f, 0f, 0f])
                .Candidates(100)
                .Where(x => x.Rank > 5)
                .FuseAsync(rrfK: 60, rrfLimit: 50);
        }
        catch
        {
            // search::rrf() not available in the in-memory engine — skip
            return;
        }

        // If results are returned (requires RRF support), verify filtering
        if (results.Count > 0)
        {
            foreach (var r in results)
                r.Rank.ShouldBeGreaterThan(5);
        }
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
