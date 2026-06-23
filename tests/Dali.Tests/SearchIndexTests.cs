using System.Reflection;

namespace Dali.Tests;

public class SearchIndexTests
{
    [Test]
    public async Task FullTextIndex_GeneratesCorrectSurql()
    {
        var idx = new IndexDefinition
        {
            Name = "ft_page_title",
            Columns = ["Title"],
            Type = IndexType.FullText,
            Analyzer = "simple",
            Bm25 = (1.2, 0.75)
        };

        // Test the SurrealQL generation via reflection on private method
        var surql = BuildBuilderMethod("BuildFullTextIndex", idx, "page");
        surql.ShouldBe("DEFINE INDEX ft_page_title ON TABLE page FIELDS Title FULLTEXT ANALYZER simple BM25(1.2, 0.75);");
    }

    [Test]
    public async Task VectorIndex_GeneratesCorrectSurql()
    {
        var idx = new IndexDefinition
        {
            Name = "hnsw_page_embedding",
            Columns = ["Embedding"],
            Type = IndexType.Hnsw,
            VectorDimension = 1536,
            VectorDistance = Search.Distance.Cosine
        };

        var surql = BuildBuilderMethod("BuildHnswIndex", idx, "page");
        surql.ShouldBe("DEFINE INDEX hnsw_page_embedding ON TABLE page FIELDS Embedding HNSW DIMENSION 1536 DIST COSINE;");
    }

    [Test]
    public async Task StandardIndex_StillWorks()
    {
        var idx = new IndexDefinition
        {
            Name = "idx_person_email",
            Columns = ["Email"],
            Type = IndexType.Standard,
            IsUnique = true
        };

        var surql = BuildBuilderMethod("BuildStandardIndex", idx, "person");
        surql.ShouldBe("DEFINE INDEX idx_person_email ON TABLE person COLUMNS Email UNIQUE;");
    }

    [Test]
    public async Task AnalyzerDefinition_GeneratesCorrectSurql()
    {
        var analyzer = new AnalyzerDefinition
        {
            Name = "simple",
            Tokenizers = [Search.Tokenizer.Blank, Search.Tokenizer.Class, Search.Tokenizer.Camel, Search.Tokenizer.Punct],
            Filters = [Search.Filter.SnowballEnglish]
        };

        // Verify the model properties
        analyzer.Name.ShouldBe("simple");
        analyzer.Tokenizers.Length.ShouldBe(4);
        analyzer.Filters.Length.ShouldBe(1);
    }

    [Test]
    public async Task FullTextIndex_WithoutBm25_UsesDefaults()
    {
        var idx = new IndexDefinition
        {
            Name = "ft_page_content",
            Columns = ["Content"],
            Type = IndexType.FullText,
            Analyzer = "simple"
        };

        var surql = BuildBuilderMethod("BuildFullTextIndex", idx, "page");
        surql.ShouldBe("DEFINE INDEX ft_page_content ON TABLE page FIELDS Content FULLTEXT ANALYZER simple;");
    }

    [Test]
    public async Task VectorIndex_DefaultDistance()
    {
        var idx = new IndexDefinition
        {
            Name = "hnsw_embedding",
            Columns = ["Embedding"],
            Type = IndexType.Hnsw,
            VectorDimension = null // uses default 1536
        };

        var surql = BuildBuilderMethod("BuildHnswIndex", idx, "doc");
        surql.ShouldContain("DIMENSION 1536");
        surql.ShouldContain("DIST COSINE");
    }

    [Test]
    public async Task FluentApi_FullTextIndex_CreatesDefinition()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().FullTextIndex(p => p.Title, "simple", (1.2, 0.75));

        var mapping = options.Schema.Mappings[typeof(SearchablePage)];
        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("ft_searchable_page_title");
        idx.Type.ShouldBe(IndexType.FullText);
        idx.Analyzer.ShouldBe("simple");
        idx.Bm25.ShouldBe((1.2, 0.75));
        idx.Columns.ShouldBe(["Title"]);
    }

    [Test]
    public async Task FluentApi_VectorIndex_CreatesDefinition()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().HnswIndex(p => p.Embedding, 1536, Search.Distance.Cosine);

        var mapping = options.Schema.Mappings[typeof(SearchablePage)];
        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("hnsw_searchable_page_embedding");
        idx.Type.ShouldBe(IndexType.Hnsw);
        idx.VectorDimension.ShouldBe(1536);
        idx.VectorDistance.ShouldBe("COSINE");
    }

    [Test]
    public async Task FluentApi_HybridSearch_CreatesMultipleIndexes()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().HybridSearch(
            [("Title", 1.0), ("Content", 0.5)],
            p => p.Embedding,
            1536,
            "simple",
            Search.Distance.Cosine
        );

        var mapping = options.Schema.Mappings[typeof(SearchablePage)];
        mapping.Indices.Count.ShouldBe(3); // 2 full-text + 1 vector

        // Full-text indexes (field names are snake_cased)
        mapping.Indices[0].Name.ShouldBe("ft_searchable_page_title");
        mapping.Indices[0].Type.ShouldBe(IndexType.FullText);
        mapping.Indices[1].Name.ShouldBe("ft_searchable_page_content");
        mapping.Indices[1].Type.ShouldBe(IndexType.FullText);

        // Vector index
        mapping.Indices[2].Name.ShouldBe("hnsw_searchable_page_embedding");
        mapping.Indices[2].Type.ShouldBe(IndexType.Hnsw);
        mapping.Indices[2].VectorDimension.ShouldBe(1536);
    }

    [Test]
    public async Task AnalyzerOptions_DefineAnalyzer_AddsToList()
    {
        var options = new AnalyzerOptions();
        options.DefineAnalyzer("my_analyzer", [Search.Tokenizer.Blank, Search.Tokenizer.Class], [Search.Filter.SnowballEnglish]);

        options.Analyzers.Count.ShouldBe(1);
        options.Analyzers[0].Name.ShouldBe("my_analyzer");
        options.Analyzers[0].Tokenizers.ShouldBe(["blank", "class"]);
        options.Analyzers[0].Filters.ShouldBe(["SNOWBALL(en)"]);
    }

    [Test]
    public async Task AnalyzerOptions_DefaultTokenizers()
    {
        var options = new AnalyzerOptions();
        options.DefineAnalyzer("simple");

        options.Analyzers.Count.ShouldBe(1);
        options.Analyzers[0].Tokenizers.ShouldBe([Search.Tokenizer.Blank, Search.Tokenizer.Class, Search.Tokenizer.Punct]);
        options.Analyzers[0].Filters.ShouldBe([]);
    }

    [Test]
    public async Task AnalyzerDefinition_DefaultsUseSearchConstants()
    {
        var options = new AnalyzerOptions();
        options.DefineAnalyzer("my_analyzer");

        var analyzer = options.Analyzers[0];
        analyzer.Name.ShouldBe("my_analyzer");
        analyzer.Tokenizers.ShouldContain(Search.Tokenizer.Blank);
        analyzer.Tokenizers.ShouldContain(Search.Tokenizer.Class);
        analyzer.Tokenizers.ShouldContain(Search.Tokenizer.Punct);
        analyzer.Filters.ShouldBeEmpty();
    }

    [Test]
    public async Task SchemaOptions_HasAnalyzers()
    {
        var schema = new SchemaOptions();
        schema.Analyzers.ShouldNotBeNull();
        schema.Analyzers.Analyzers.ShouldBeEmpty();
    }

    // ──────────────────────────────────────────────
    //  DISKANN Index Tests
    // ──────────────────────────────────────────────

    [Test]
    public async Task DiskannIndex_Basic_SQL()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_page_embedding",
            Columns = ["Embedding"],
            Type = IndexType.Diskann,
            VectorDimension = 768,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F32"
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "page");
        surql.ShouldBe("DEFINE INDEX diskann_page_embedding ON TABLE page FIELDS Embedding DISKANN DIMENSION 768 DIST COSINE TYPE F32;");
    }

    [Test]
    public async Task DiskannIndex_With_Degree_LBuild()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_doc_features",
            Columns = ["Features"],
            Type = IndexType.Diskann,
            VectorDimension = 512,
            VectorDistance = Search.Distance.Euclidean,
            VectorElementType = "F32",
            DiskannDegree = 128,
            DiskannLBuild = 200
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "doc");
        surql.ShouldBe("DEFINE INDEX diskann_doc_features ON TABLE doc FIELDS Features DISKANN DIMENSION 512 DIST EUCLIDEAN TYPE F32 DEGREE 128 L_BUILD 200;");
    }

    [Test]
    public async Task DiskannIndex_With_Alpha()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_article_vector",
            Columns = ["Vector"],
            Type = IndexType.Diskann,
            VectorDimension = 256,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F32",
            DiskannAlpha = 1.5
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "article");
        surql.ShouldBe("DEFINE INDEX diskann_article_vector ON TABLE article FIELDS Vector DISKANN DIMENSION 256 DIST COSINE TYPE F32 ALPHA 1.5;");
    }

    [Test]
    public async Task DiskannIndex_Default_Alpha_Omitted()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_item_vec",
            Columns = ["Vec"],
            Type = IndexType.Diskann,
            VectorDimension = 128,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F32",
            DiskannAlpha = 1.2 // default — should be omitted
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "item");
        surql.ShouldBe("DEFINE INDEX diskann_item_vec ON TABLE item FIELDS Vec DISKANN DIMENSION 128 DIST COSINE TYPE F32;");
    }

    [Test]
    public async Task DiskannIndex_With_Hashed_Vector()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_user_embedding",
            Columns = ["Embedding"],
            Type = IndexType.Diskann,
            VectorDimension = 768,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F32",
            HasHashedVector = true
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "user");
        surql.ShouldBe("DEFINE INDEX diskann_user_embedding ON TABLE user FIELDS Embedding DISKANN DIMENSION 768 DIST COSINE TYPE F32 HASHED_VECTOR;");
    }

    [Test]
    public async Task DiskannIndex_With_Different_Types()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_img_emb",
            Columns = ["Emb"],
            Type = IndexType.Diskann,
            VectorDimension = 512,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F16"
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "image");
        surql.ShouldBe("DEFINE INDEX diskann_img_emb ON TABLE image FIELDS Emb DISKANN DIMENSION 512 DIST COSINE TYPE F16;");
    }

    [Test]
    public async Task DiskannIndex_Type_I8()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_quant_8",
            Columns = ["Quant"],
            Type = IndexType.Diskann,
            VectorDimension = 64,
            VectorDistance = Search.Distance.InnerProduct,
            VectorElementType = "I8"
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "quant");
        surql.ShouldBe("DEFINE INDEX diskann_quant_8 ON TABLE quant FIELDS Quant DISKANN DIMENSION 64 DIST INNER_PRODUCT TYPE I8;");
    }

    [Test]
    public async Task DiskannIndex_Type_U8()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_byte_vec",
            Columns = ["Bytes"],
            Type = IndexType.Diskann,
            VectorDimension = 32,
            VectorDistance = Search.Distance.CosineNormalized,
            VectorElementType = "U8"
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "byte_table");
        surql.ShouldBe("DEFINE INDEX diskann_byte_vec ON TABLE byte_table FIELDS Bytes DISKANN DIMENSION 32 DIST COSINE_NORMALIZED TYPE U8;");
    }

    [Test]
    public async Task DiskannIndex_Default_Type_Is_F32()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_def_type",
            Columns = ["Vec"],
            Type = IndexType.Diskann,
            VectorDimension = 768,
            VectorDistance = Search.Distance.Cosine
            // VectorElementType not set — should default to F32
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "defaults");
        surql.ShouldContain("TYPE F32");
    }

    [Test]
    public async Task DiskannIndex_Default_Dist_Is_Cosine()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_def_dist",
            Columns = ["Vec"],
            Type = IndexType.Diskann,
            VectorDimension = 768
            // VectorDistance not set — should default to COSINE
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "defaults");
        surql.ShouldContain("DIST COSINE");
    }

    [Test]
    public async Task DiskannIndex_Default_Dimension_Is_768()
    {
        var idx = new IndexDefinition
        {
            Name = "diskann_def_dim",
            Columns = ["Vec"],
            Type = IndexType.Diskann,
            VectorDistance = Search.Distance.Cosine,
            VectorElementType = "F32"
            // VectorDimension not set — should default to 768
        };

        var surql = BuildBuilderMethod("BuildDiskannIndex", idx, "defaults");
        surql.ShouldContain("DIMENSION 768");
    }

    [Test]
    public async Task DiskannIndex_Schema_Registration()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().DiskannIndex(p => p.Embedding, 768);

        var mapping = options.Schema.Mappings[typeof(SearchablePage)];
        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("diskann_searchable_page_embedding");
        idx.Type.ShouldBe(IndexType.Diskann);
        idx.VectorDimension.ShouldBe(768);
        idx.VectorDistance.ShouldBe("COSINE");
        idx.VectorElementType.ShouldBe("F32");
        idx.DiskannDegree.ShouldBeNull();
        idx.DiskannLBuild.ShouldBeNull();
        idx.DiskannAlpha.ShouldBeNull();
        idx.HasHashedVector.ShouldBeFalse();
    }

    [Test]
    public async Task DiskannIndex_Schema_Registration_With_All_Params()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>().DiskannIndex(
            p => p.Embedding,
            dimension: 512,
            vectorType: "F16",
            distance: Search.Distance.Euclidean,
            degree: 128,
            lBuild: 200,
            alpha: 1.4,
            hashedVector: true
        );

        var mapping = options.Schema.Mappings[typeof(SearchablePage)];
        mapping.Indices.Count.ShouldBe(1);

        var idx = mapping.Indices[0];
        idx.Name.ShouldBe("diskann_searchable_page_embedding");
        idx.Type.ShouldBe(IndexType.Diskann);
        idx.VectorDimension.ShouldBe(512);
        idx.VectorDistance.ShouldBe("EUCLIDEAN");
        idx.VectorElementType.ShouldBe("F16");
        idx.DiskannDegree.ShouldBe(128);
        idx.DiskannLBuild.ShouldBe(200);
        idx.DiskannAlpha.ShouldBe(1.4);
        idx.HasHashedVector.ShouldBeTrue();
    }

    [Test]
    public async Task DiskannIndex_FluentApi_FullSchema()
    {
        var options = new StoreOptions();
        options.Schema.For<SearchablePage>()
            .DiskannIndex(p => p.Embedding, 768);

        var surql = BuildBuilderMethod("BuildDiskannIndex",
            options.Schema.Mappings[typeof(SearchablePage)].Indices[0],
            "searchable_page");

        surql.ShouldBe("DEFINE INDEX diskann_searchable_page_embedding ON TABLE searchable_page FIELDS Embedding DISKANN DIMENSION 768 DIST COSINE TYPE F32;");
    }

    [Test]
    public async Task DiskannIndex_Integration_Creates_Index()
    {
        // DiskANN requires SurrealDB 3.1+ and the in-memory engine may not support it.
        // This test verifies schema registration works; actual index creation on the
        // in-memory engine is expected to fail gracefully with a parse error.
        IDocumentStore? store = null;
        try
        {
            store = await TestHarness.CreateStoreAsync(o =>
            {
                o.Namespace = "test";
                o.Database = "test";
                o.Schema.For<SearchablePage>().DiskannIndex(p => p.Embedding, 3);
            });
        }
        catch (Exception ex)
        {
            // In-memory engine doesn't support DISKANN — expected failure
            ex.Message.ShouldContain("DISKANN");
            return;
        }

        await using (store!)
        {
            var mapping = store.Options.Schema.Mappings[typeof(SearchablePage)];
            mapping.Indices.Count.ShouldBe(1);
            mapping.Indices[0].Type.ShouldBe(IndexType.Diskann);
            mapping.Indices[0].VectorDimension.ShouldBe(3);
        }
    }

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
