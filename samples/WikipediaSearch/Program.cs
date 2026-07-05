using System.Diagnostics;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;

// ──────────────────────────────────────────────────────────────
//  AeroDB Wikipedia Search Demo
//  Fetches 20 random Wikipedia articles, generates real ONNX
//  embeddings, and exercises all of AeroDB's search capabilities:
//  FTS, HNSW, DiskANN, Hybrid, and raw SurrealQL.
//
//  Usage:
//    dotnet run            — connect to local SurrealDB at localhost:8000
//    dotnet run embedded   — use embedded SurrealKv engine (no server needed)
// ──────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
var totalSw = Stopwatch.StartNew();

var mode = (args.Length > 0 ? args[0] : "").ToLowerInvariant();
Console.WriteLine(mode == "embedded"
    ? "[Init] Mode: embedded (SurrealKv)"
    : "[Init] Mode: server (ws://localhost:8000)");

// ── 0. Print SurrealDB version ──
await PrintVersionAsync(mode);

// ── 1. Load ONNX embedding model ──
var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "all-MiniLM-L6-v2");
Console.WriteLine($"[Init] Loading ONNX model from {modelDir}...");

using var embedder = new EmbeddingGenerator(modelDir);
Console.WriteLine($"[Init] Model ready — {embedder.Dimension}-dim embeddings\n");

// ── 2. Fetch Wikipedia articles in parallel ──
Console.WriteLine("[Fetch] Retrieving 20 random Wikipedia articles in parallel...\n");
var fetchSw = Stopwatch.StartNew();
var articles = await WikipediaFetcher.FetchRandomBatchAsync(20);

fetchSw.Stop();
Console.WriteLine($"\n[Fetch] Got {articles.Length} unique articles in {fetchSw.ElapsedMilliseconds}ms");

if (articles.Length == 0)
{
    Console.WriteLine("[Error] No articles retrieved. Check your internet connection.");
    return 1;
}

foreach (var a in articles.Take(3))
    Console.WriteLine($"  - \"{a.Title}\" ({a.Content.Length} chars)");
if (articles.Length > 3)
    Console.WriteLine($"  ... and {articles.Length - 3} more");
Console.WriteLine();

// ── 3. Run search demos ──
await SearchDemos.RunAllAsync(articles, embedder, mode);

totalSw.Stop();
Console.WriteLine($"Total time: {totalSw.ElapsedMilliseconds}ms");
return 0;

// ──────────────────────────────────────────────────────────────
//  Helpers
// ──────────────────────────────────────────────────────────────

static async Task PrintVersionAsync(string mode)
{
    try
    {
        string version;
        if (mode == "embedded")
        {
            var tmpDir = $"{Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)}{Path.DirectorySeparatorChar}aerodb_ver_{Guid.NewGuid():N}"
                .Replace(Path.DirectorySeparatorChar, '/');
            await using var client = new SurrealDbKvClient(tmpDir);
            version = await client.Version();
            Console.WriteLine($"[Version] SurrealDB {version} (embedded SurrealKv)");
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
        else
        {
            await using var client = new SurrealDbClient("ws://localhost:8000/rpc");
            version = await client.Version();
            Console.WriteLine($"[Version] SurrealDB {version} (server)");
        }

        // Parse major.minor and warn if < 3.1 (DiskANN unavailable)
        var parsed = ParseVersion(version);
        if (parsed is not null && (parsed.Value.Major < 3 || (parsed.Value.Major == 3 && parsed.Value.Minor < 1)))
        {
            Console.WriteLine();
            Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  WARNING: SurrealDB 3.0.x detected.                         ║");
            Console.WriteLine("  ║  DiskANN vector indexes require SurrealDB 3.1 or higher.    ║");
            Console.WriteLine("  ║  Upgrade your server: https://surrealdb.com/releases        ║");
            Console.WriteLine("  ║  The demo will fall back to HNSW-only search.               ║");
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Version] Could not determine: {ex.Message.Split('\n')[0]}\n");
    }
}

static (int Major, int Minor)? ParseVersion(string version)
{
    // Version strings: "surrealdb-3.1.0", "3.0.5", "3.1.0-beta.1"
    var match = System.Text.RegularExpressions.Regex.Match(version, @"(\d+)\.(\d+)");
    if (match.Success)
    {
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        return (major, minor);
    }
    return null;
}
