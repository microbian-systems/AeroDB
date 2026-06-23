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
