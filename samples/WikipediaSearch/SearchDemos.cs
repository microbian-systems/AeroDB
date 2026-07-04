using AeroDB;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;
using SurrealDb.Net.Models;

// ────────────────────────────────────────────────────
//  Model
// ────────────────────────────────────────────────────

public class WikiArticle : Record
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public float[] Embedding { get; set; } = [];
}

// ────────────────────────────────────────────────────
//  Demos
// ────────────────────────────────────────────────────

public static class SearchDemos
{
    /// <summary>
    /// Run all search demos against a collection of Wikipedia articles
    /// with real ONNX embeddings.
    /// </summary>
    public static async Task RunAllAsync(
        WikipediaFetcher.Article[] articles,
        EmbeddingGenerator embedder,
        string mode = "",
        CancellationToken ct = default)
    {
        Console.WriteLine($"\n=== Search Demos ({articles.Length} articles) ===\n");

        string[] storeDirs = [];
        try
        {
            var (store, dirs) = await BuildStore(embedder.Dimension, mode);
            storeDirs = dirs;
            await using var _store = store;

            await using var session = await store.OpenSessionAsync(
                new SessionOptions { Tracking = DocumentTracking.None });

            // ── Generate embeddings & store ──
            Console.WriteLine("  [Embedding] Generating embeddings for all articles...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var embeddings = embedder.GenerateBatch(articles.Select(a => a.Content).ToArray()!);
            sw.Stop();
            Console.WriteLine($"  [Embedding] Done — {articles.Length} embeddings in {sw.ElapsedMilliseconds}ms");

            for (int i = 0; i < articles.Length; i++)
            {
                session.Store(new WikiArticle
                {
                    Title = articles[i].Title,
                    Content = articles[i].Content,
                    Embedding = embeddings[i]
                });
            }
            await session.SaveChangesAsync();
            Console.WriteLine($"  [Store] Stored {articles.Length} articles.\n");

            // ── Run demos (each individually catchable so one failure doesn't stop all) ──
            async Task RunSafe(string name, Func<Task> demo)
            {
                try { await demo(); }
                catch (Exception ex) { Console.WriteLine($"  [{name}] FAILED: {ex.Message}\n"); }
            }

            await RunSafe("FullText",           () => DemoFullTextAsync(session, articles, ct));
            await RunSafe("HNSW Vector",        () => DemoHnswVectorAsync(session, articles, embeddings, ct));
            await RunSafe("DiskANN",            () => DemoDiskAnnVectorAsync(session, articles, embeddings, ct));
            await RunSafe("Hybrid",             () => DemoHybridAsync(session, articles, embeddings, ct));
            await RunSafe("PlainPrefixPhrase",  () => DemoPlainPrefixPhraseAsync(session, articles, ct));
            await RunSafe("Raw SurrealQL",      () => DemoRawSurrealQLAsync(session, articles, ct));

            Console.WriteLine("\n=== All demos complete ===\n");
        }
        finally
        {
            foreach (var d in storeDirs)
                try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    // ────────────────────────────────────────────────────
    //  Store setup
    // ────────────────────────────────────────────────────

    private static async Task<(IDocumentStore Store, string[] TempDirs)> BuildStore(int dim, string mode)
    {
        if (mode != "embedded")
        {
            try
            {
                return await BuildServerStore(dim);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [Init] Server connection failed — {ex.Message.Split('\n')[0]}");
                if (ex.Message.Contains("DISKANN") || ex.Message.Contains("Parse error"))
                    Console.WriteLine("  [Init] DiskANN requires SurrealDB 3.1+. Upgrade your server from https://surrealdb.com/releases.");
                Console.WriteLine("  [Init] Falling back to embedded SurrealKv engine.\n");
            }
        }

        return await BuildEmbeddedStore(dim);
    }

    private static async Task<(IDocumentStore Store, string[] TempDirs)> BuildServerStore(int dim)
    {
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbClient("ws://localhost:8000/rpc");
            o.Namespace = "demo";
            o.Database = "demo";

            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.English);

            o.Schema.For<WikiArticle>()
                .FullTextIndex(a => a.Content, Search.Analyzer.English)
                .FullTextIndex(a => a.Title, Search.Analyzer.Simple)
                .HnswIndex(a => a.Embedding, dim, Search.Distance.Cosine)
                .DiskannIndex(a => a.Embedding, dim,
                    distance: Search.Distance.Cosine,
                    vectorType: "F32",
                    degree: 64,
                    lBuild: 128);
        });

        await store.InitializeAsync();
        var version = await store.GetVersionAsync();
        Console.WriteLine($"  [Store] SurrealDB version: {version ?? "unknown"}");
        return (store, []); // no temp dirs to clean up on server mode
    }

    private static async Task<(IDocumentStore Store, string[] TempDirs)> BuildEmbeddedStore(int dim)
    {
        var dirs = new List<string>();

        string CreateTempDir()
        {
            var d = $"{Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)}{Path.DirectorySeparatorChar}aerodb_{Guid.NewGuid():N}"
                .Replace(Path.DirectorySeparatorChar, '/');
            dirs.Add(d);
            return d;
        }

        var dir = CreateTempDir();

        IDocumentStore store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbKvClient(dir);
            o.Namespace = "demo";
            o.Database = "demo";

            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.English);

            o.Schema.For<WikiArticle>()
                .FullTextIndex(a => a.Content, Search.Analyzer.English)
                .FullTextIndex(a => a.Title, Search.Analyzer.Simple)
                .HnswIndex(a => a.Embedding, dim, Search.Distance.Cosine)
                .DiskannIndex(a => a.Embedding, dim,
                    distance: Search.Distance.Cosine,
                    vectorType: "F32",
                    degree: 64,
                    lBuild: 128);
        });

        try
        {
            await store.InitializeAsync();
            var version = await store.GetVersionAsync();
            Console.WriteLine($"  [Store] SurrealDB version: {version ?? "unknown"}");
        }
        catch (SurrealDb.Net.Exceptions.Embedded.SurrealDbEmbeddedException ex)
            when (ex.Message.Contains("DISKANN"))
        {
            Console.WriteLine("  [Store] DiskANN requires SurrealDB 3.1+ — retrying without it.");
            await store.DisposeAsync();

            try { Directory.Delete(dir, recursive: true); } catch { }
            dirs.Remove(dir);

            var fbDir = CreateTempDir();

            store = Documents.For(o =>
            {
                o.ClientFactory = () => new SurrealDbKvClient(fbDir);
                o.Namespace = "demo";
                o.Database = "demo";
                o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
                o.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.English);
                o.Schema.For<WikiArticle>()
                    .FullTextIndex(a => a.Content, Search.Analyzer.English)
                    .FullTextIndex(a => a.Title, Search.Analyzer.Simple)
                    .HnswIndex(a => a.Embedding, dim, Search.Distance.Cosine);
            });
            await store.InitializeAsync();
            var version = await store.GetVersionAsync();
            Console.WriteLine($"  [Store] SurrealDB version: {version ?? "unknown"}");
        }

        return (store, dirs.ToArray());
    }

    // ────────────────────────────────────────────────────
    //  Demo 1: Full-Text Search
    // ────────────────────────────────────────────────────

    private static async Task DemoFullTextAsync(
        IQuerySession session, WikipediaFetcher.Article[] articles,
        CancellationToken ct)
    {
        Header("Full-Text Search");

        var query = PickDistinctiveWord(articles[0].Title);
        Console.WriteLine($"  Query: \"{query}\"");

        // Single-field
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var results = await session.Query<WikiArticle>()
            .MatchTextAsync(a => a.Content, query);
        sw1.Stop();
        PrintResults("MatchTextAsync (Content)", results, sw1.ElapsedMilliseconds);

        // Multi-field weighted
        var multiFields = new (System.Linq.Expressions.Expression<Func<WikiArticle, string>> Field, double Weight)[]
        {
            (a => a.Title, 3.0),
            (a => a.Content, 1.0)
        };
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var multiResults = await session.Query<WikiArticle>()
            .MatchTextAsync(multiFields, query);
        sw2.Stop();
        PrintResults("MatchTextAsync (Title x3 + Content)", multiResults, sw2.ElapsedMilliseconds);
    }

    // ────────────────────────────────────────────────────
    //  Demo 2: HNSW Vector Search
    // ────────────────────────────────────────────────────

    private static async Task DemoHnswVectorAsync(
        IQuerySession session,
        WikipediaFetcher.Article[] articles,
        float[][] embeddings, CancellationToken ct)
    {
        Header("HNSW Vector Search (Cosine)");

        var queryVec = embeddings[0];
        Console.WriteLine($"  Query vector: embedding of \"{articles[0].Title}\"");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await session.Query<WikiArticle>()
            .MatchKnnAsync(a => a.Embedding, queryVec, limit: 5, candidates: 50);
        sw.Stop();
        PrintResults("MatchKnnAsync (HNSW)", results, sw.ElapsedMilliseconds);
        Console.WriteLine($"  Top match: \"{results.FirstOrDefault()?.Title}\"");
    }

    // ────────────────────────────────────────────────────
    //  Demo 4: DiskANN Vector Search
    // ────────────────────────────────────────────────────

    private static async Task DemoDiskAnnVectorAsync(
        IQuerySession session,
        WikipediaFetcher.Article[] articles,
        float[][] embeddings, CancellationToken ct)
    {
        Header("DiskANN Vector Search (SurrealDB 3.1+, not in embedded engine)");

        var queryVec = embeddings[2];
        Console.WriteLine($"  Query vector: embedding of \"{articles[2].Title}\"");

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await session.Query<WikiArticle>()
                .MatchKnnAsync(a => a.Embedding, queryVec, limit: 5, candidates: 50);
            sw.Stop();
            PrintResults("MatchKnnAsync (DiskANN)", results, sw.ElapsedMilliseconds);
            Console.WriteLine($"  Top match: \"{results.FirstOrDefault()?.Title}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  DiskANN not available on this engine: {ex.Message.Split('\n')[0]}");
        }
    }

    // ────────────────────────────────────────────────────
    //  Demo 5: Hybrid Search (FTS + Vector RRF)
    // ────────────────────────────────────────────────────

    private static async Task DemoHybridAsync(
        IQuerySession session,
        WikipediaFetcher.Article[] articles,
        float[][] embeddings, CancellationToken ct)
    {
        Header("Hybrid Search (FTS + HNSW via RRF)");

        var queryText = PickDistinctiveWord(articles[0].Title);
        var queryVec = embeddings[0];
        Console.WriteLine($"  Text: \"{queryText}\", Vector: embedding of \"{articles[0].Title}\"");

        var config = new HybridSearchConfig
        {
            Query = queryText,
            QueryVector = queryVec,
            VectorField = "Embedding",
            TextFields = [("Content", 1.0)],
            VectorCandidates = 50,
            RrfK = 60,
            RrfLimit = 10
        };

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await session.Query<WikiArticle>()
                .HybridSearchAsync(config);
            sw.Stop();
            PrintResults("HybridSearchAsync (RRF fusion)", results, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Hybrid search threw: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────
    //  Demo 6: Plain / Prefix / Phrase / Web-Style
    // ────────────────────────────────────────────────────

    private static async Task DemoPlainPrefixPhraseAsync(
        IQuerySession session,
        WikipediaFetcher.Article[] articles, CancellationToken ct)
    {
        Header("Plain / Prefix / Phrase / Web-Style Search");

        var word = PickDistinctiveWord(articles[0].Title);
        Console.WriteLine($"  Target word: \"{word}\"");

        // Plain text search
        try
        {
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            var plain = await session.RawQueryAsync<WikiArticle>(
                "SELECT * FROM wiki_article WHERE Content CONTAINS $query LIMIT 5",
                new Dictionary<string, object?> { ["query"] = word });
            sw1.Stop();
            PrintResults("Raw CONTAINS (plain text)", plain, sw1.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  CONTAINS query threw: {ex.Message}");
        }

        // Prefix search
        var prefix = word.Length > 4 ? word[..4] : word;
        Console.WriteLine($"  Prefix search: \"{prefix}*\"");

        // Phrase search
        var phrase = ExtractPhrase(articles[0].Content, word);
        if (phrase is not null)
        {
            Console.WriteLine($"  Phrase search: \"{phrase}\"");
            try
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var phraseResults = await session.RawQueryAsync<WikiArticle>(
                    "SELECT * FROM wiki_article WHERE Content CONTAINS $phrase LIMIT 5",
                    new Dictionary<string, object?> { ["phrase"] = $"\"{phrase}\"" });
                sw2.Stop();
                PrintResults("Quoted phrase CONTAINS", phraseResults, sw2.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Phrase query threw: {ex.Message}");
            }
        }
    }

    // ────────────────────────────────────────────────────
    //  Demo 7: Raw SurrealQL
    // ────────────────────────────────────────────────────

    private static async Task DemoRawSurrealQLAsync(
        IQuerySession session,
        WikipediaFetcher.Article[] articles, CancellationToken ct)
    {
        Header("Raw SurrealQL Queries");

        // FTS with scoring
        var query = PickDistinctiveWord(articles[0].Title);
        Console.WriteLine($"  FTS (scored) for: \"{query}\"");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ftsScored = await session.RawQueryAsync<WikiArticle>(
                "SELECT *, search::score(0) AS _score FROM wiki_article " +
                "WHERE Content @@ $query ORDER BY _score DESC LIMIT 5",
                new Dictionary<string, object?> { ["query"] = query });
            sw.Stop();
            PrintResults("Raw SurrealQL @@ with search::score()", ftsScored, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Raw @@ query threw: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────

    private static void Header(string name)
    {
        Console.WriteLine($"═══ {name} ═══");
    }

    private static void PrintResults(string label, List<WikiArticle>? results, long elapsedMs = -1)
    {
        var time = elapsedMs >= 0 ? $" ({elapsedMs}ms)" : "";
        Console.WriteLine($"  [{label}] {results?.Count ?? 0} result(s){time}:");
        if (results is { Count: > 0 })
        {
            foreach (var r in results.Take(5))
                Console.WriteLine($"    - {r.Title}");
        }
        Console.WriteLine();
    }

    private static string PickDistinctiveWord(string title)
    {
        var words = title.Split([' ', '_', '(', ')', ',', '-'],
            StringSplitOptions.RemoveEmptyEntries);
        return words.OrderByDescending(w => w.Length).FirstOrDefault() ?? title;
    }

    private static string? ExtractPhrase(string content, string word)
    {
        var idx = content.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Walk back to start of nearby words
        var start = idx;
        var wordsBack = 0;
        while (start > 0 && wordsBack < 2)
        {
            start--;
            if (content[start] == ' ') wordsBack++;
        }
        while (start < idx && content[start] == ' ') start++;

        // Walk forward ~4 words
        var end = idx + word.Length;
        var wordsFwd = 0;
        while (end < content.Length && wordsFwd < 4)
        {
            if (content[end] == ' ') wordsFwd++;
            end++;
        }

        var phrase = content[start..end].Trim(' ', '.', ',', ';', ':');
        return phrase.Length > 10 ? phrase : null;
    }
}
