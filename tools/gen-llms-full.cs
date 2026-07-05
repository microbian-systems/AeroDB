// tools/gen-llms-full.cs
// Usage: dotnet run tools/gen-llms-full.cs [--split] [--out-dir <path>]
// Generates aerodb-llms.txt and aerodb-llms-full.txt (or split files with --split)
//
// Sources:
//   1. XML doc files from Release builds (public API with summaries, params, returns)
//   2. Markdown docs from docs/src/content/docs/ (guides, concepts, recipes, examples)
//   3. Sample code from samples/ (.cs files wrapped in code blocks)
//   4. Root project files (AGENTS.md, README.md)
//
// Section generation runs in parallel via Task.Run, then compiled into aerodb-llms-full.txt.

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

var outDir = "docs/dist";
var doSplit = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--split") doSplit = true;
    else if (args[i] == "--out-dir" && i + 1 < args.Length) outDir = args[++i];
}

var repoRoot = Environment.CurrentDirectory;
while (!Directory.Exists(Path.Combine(repoRoot, ".git")) && !File.Exists(Path.Combine(repoRoot, ".git")))
{
    var parent = Path.GetDirectoryName(repoRoot);
    if (parent is null || parent == repoRoot) break;
    repoRoot = parent;
}

var sep = "\n\n---\n\n";

// Parse version from Directory.Build.props
var buildProps = Path.Combine(repoRoot, "src", "Directory.Build.props");
var version = "0.0.0";
if (File.Exists(buildProps))
{
    var xml = XDocument.Load(buildProps);
    var prefix = xml.Descendants("VersionPrefix").FirstOrDefault()?.Value.Trim();
    var suffix = xml.Descendants("VersionSuffix").FirstOrDefault()?.Value.Trim();
    version = string.IsNullOrEmpty(suffix) ? prefix : $"{prefix}-{suffix}";
}
var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
var header = $"# AeroDB v{version} — Documentation generated {now}\n\n";

var footer = "\n\n---\n\nMade with 💜 by Microbians — Copyright © 2026 — https://microbians.io/\n";
var docDir = Path.Combine(repoRoot, "docs", "src", "content", "docs");

// ── Helpers ───────────────────────────────────────────────────

string StripFrontMatter(string content)
{
    if (content.StartsWith("---"))
    {
        var end = content.IndexOf("\n---", 3);
        if (end > 0) return content[(end + 4)..].TrimStart();
    }
    return content;
}

string FormatBytes(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
    _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
};

// ── Collect markdown files by category ────────────────────────

var categories = new Dictionary<string, List<string>>
{
    ["getting-started"] = [], ["concepts"]  = [], ["guides"]    = [],
    ["recipes"]         = [], ["examples"]  = [], ["integrations"] = [],
    ["advanced"]        = [], ["faq"]       = [], ["roadmap"]      = []
};

foreach (var file in Directory.EnumerateFiles(docDir, "*.md", SearchOption.AllDirectories)
    .OrderBy(f => f))
{
    var rel = Path.GetRelativePath(docDir, file).Replace('\\', '/');
    var cat = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "root";
    if (categories.ContainsKey(cat))
        categories[cat].Add(file);
}

// ── Parallel section generators ───────────────────────────────

// Build markdown content for a category (deduplicates inline code blocks)
async Task<string> BuildMdSection(string cat)
{
    return await Task.Run(() =>
    {
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var file in categories[cat])
        {
            var content = File.ReadAllText(file);
            var slug = Path.GetFileNameWithoutExtension(file);
            sb.AppendLine($"## {cat}/{slug}");
            sb.AppendLine();
            sb.AppendLine(StripFrontMatter(content));
            sb.AppendLine(sep);
        }
        return sb.ToString();
    });
}

// Build combined guides section (guides + recipes)
async Task<string> BuildGuidesSection() =>
    await BuildMdSection("guides") + await BuildMdSection("recipes");

// Build examples section (docs + sample .cs files)
async Task<string> BuildExamplesSection()
{
    var md = await BuildMdSection("examples");
    var sb = new StringBuilder(md);

    var samplesDir = Path.Combine(repoRoot, "samples");
    if (Directory.Exists(samplesDir))
    {
        foreach (var csFile in Directory.EnumerateFiles(samplesDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\Tests\\"))
            .OrderBy(f => f)
            .Take(100))
        {
            var rel = Path.GetRelativePath(repoRoot, csFile).Replace('\\', '/');
            sb.AppendLine($"### samples/{rel}");
            sb.AppendLine("```csharp");
            sb.AppendLine(File.ReadAllText(csFile));
            sb.AppendLine("```");
            sb.AppendLine(sep);
        }
    }
    return sb.ToString();
}

// Build API section (parse XML doc files from Release builds)
async Task<string> BuildApiSection()
{
    return await Task.Run(() =>
    {
        var xmlDir = Path.Combine(repoRoot, "src");
        var sb = new StringBuilder();
        var xdocFiles = Directory.EnumerateFiles(xmlDir, "*.xml", SearchOption.AllDirectories)
            // Include: Release build output XML, exclude source generators and tests
            .Where(f => f.Contains("Release")
                && !f.Contains("SourceGenerators")
                && !f.Contains("Tests")
                && !f.Contains("\\obj\\"))
            // Keep: assembly-specific XML files (AeroDB.Foo.xml), plus main AeroDB.xml
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith("AeroDB.") && name.EndsWith(".xml");
            })
            .Distinct()
            .OrderBy(f => f);

        foreach (var xdocPath in xdocFiles)
        {
            var asmName = Path.GetFileNameWithoutExtension(
                Path.GetDirectoryName(Path.GetDirectoryName(xdocPath))!);
            sb.AppendLine($"### {asmName}");
            sb.AppendLine();

            try
            {
                var doc = XDocument.Load(xdocPath);
                var members = doc.Descendants("member")
                    .OrderBy(m => m.Attribute("name")?.Value ?? "");

                foreach (var member in members)
                {
                    var name = member.Attribute("name")?.Value ?? "";
                    var prefix = name.Length > 0 ? name[0] : ' ';
                    var summary = member.Element("summary")?.Value.Trim();
                    var paramDocs = member.Elements("param")
                        .Select(p => $"  * `{p.Attribute("name")?.Value}`: {p.Value.Trim()}")
                        .ToArray();
                    var returns = member.Element("returns")?.Value.Trim();

                    if (prefix == 'T')
                    {
                        var typeName = name[2..];
                        sb.AppendLine($"**{typeName}**");
                        if (!string.IsNullOrWhiteSpace(summary))
                            sb.AppendLine(summary);
                        sb.AppendLine();
                    }
                    else if (prefix == 'M')
                    {
                        var sig = name[2..];
                        var shortName = sig.Contains('(') ? sig[(sig.LastIndexOf('.', sig.IndexOf('(')) + 1)..] : sig;
                        sb.AppendLine($"- `{shortName}`");
                        if (!string.IsNullOrWhiteSpace(summary)) sb.AppendLine($"  {summary}");
                        foreach (var p in paramDocs) sb.AppendLine(p);
                        if (!string.IsNullOrWhiteSpace(returns)) sb.AppendLine($"  → {returns}");
                    }
                    else if (prefix == 'P')
                    {
                        var propName = name[2..];
                        var shortName = propName.Contains('.') ? propName[(propName.LastIndexOf('.') + 1)..] : propName;
                        sb.AppendLine($"- `{shortName}` (property)");
                        if (!string.IsNullOrWhiteSpace(summary)) sb.AppendLine($"  {summary}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"> Error parsing {Path.GetFileName(xdocPath)}: {ex.Message}");
            }
            sb.AppendLine(sep);
        }
        return sb.ToString();
    });
}

// Build advanced section (advanced + integrations)
async Task<string> BuildAdvancedSection() =>
    await BuildMdSection("advanced") + await BuildMdSection("integrations");

// ── Launch all sections in parallel ────────────────────────────

var start = DateTime.Now;
Console.WriteLine("Building sections in parallel...");

var tasks = new Dictionary<string, Task<string>>
{
    ["getting-started"]  = BuildMdSection("getting-started"),
    ["concepts"]         = BuildMdSection("concepts"),
    ["guides"]           = BuildGuidesSection(),
    ["examples"]         = BuildExamplesSection(),
    ["api"]              = BuildApiSection(),
    ["advanced"]         = BuildAdvancedSection(),
    ["faq"]              = BuildMdSection("faq"),
    ["troubleshooting"]  = BuildMdSection("faq"), // same source
};

await Task.WhenAll(tasks.Values);

var splitContent = tasks.ToDictionary(kv => kv.Key, kv => kv.Value.Result);

var elapsed = (DateTime.Now - start).TotalSeconds;
Console.WriteLine($"Sections built in {elapsed:F1}s");
Console.WriteLine();

// ── Compile llms-full.txt ──────────────────────────────────────

var order = new[] { "getting-started", "concepts", "guides", "examples", "api", "advanced", "faq", "troubleshooting" };
var fullContent = new StringBuilder();

foreach (var rootFile in new[] { "README.md", "AGENTS.md" })
{
    var path = Path.Combine(repoRoot, rootFile);
    if (File.Exists(path))
    {
        fullContent.AppendLine(StripFrontMatter(File.ReadAllText(path)));
        fullContent.AppendLine(sep);
    }
}

foreach (var section in order)
{
    if (splitContent.TryGetValue(section, out var content) && !string.IsNullOrEmpty(content))
        fullContent.AppendLine(content);
}

// ── Write output ──────────────────────────────────────────────

Directory.CreateDirectory(outDir);

var fullPath = Path.Combine(outDir, "aerodb-llms-full.txt");
await File.WriteAllTextAsync(fullPath, header + fullContent.ToString() + footer);
Console.WriteLine($"  aerodb-llms-full.txt = {FormatBytes(new FileInfo(fullPath).Length)}");

if (doSplit)
{
    var totalSeen = new HashSet<string>();
    foreach (var (section, content) in splitContent)
    {
        if (content.Length == 0) continue;
        var path = Path.Combine(outDir, $"aerodb-llms-{section}.txt");
        // Global dedup only for split files
        var deduped = DeduplicateCodeBlocks(content, totalSeen);
        await File.WriteAllTextAsync(path, header + deduped + footer);
        Console.WriteLine($"  aerodb-llms-{section,-18}.txt = {FormatBytes(new FileInfo(path).Length)}");
    }

    // Entry point TOC
    var tocPath = Path.Combine(outDir, "aerodb-llms.txt");
    var toc = new StringBuilder();
    toc.AppendLine("# AeroDB Documentation (AI-Optimized)");
    toc.AppendLine();
    toc.AppendLine("High-performance multi-model document database for .NET, built on SurrealDB.");
    toc.AppendLine();
    toc.AppendLine("## Available Documents");
    toc.AppendLine();
    foreach (var section in order)
    {
        if (splitContent.TryGetValue(section, out var c) && c.Length > 0)
            toc.AppendLine($"- [aerodb-llms-{section}.txt](aerodb-llms-{section}.txt)");
    }
    toc.AppendLine($"- [aerodb-llms-full.txt](aerodb-llms-full.txt)");
    toc.AppendLine();
    toc.AppendLine("## Quick Reference");
    toc.AppendLine();
    toc.AppendLine("- **Installation**: `dotnet add package AeroDB`");
    toc.AppendLine("- **Configuration**: `DocumentStore.For(cfg => cfg.Connection(\"...\"))`");
    toc.AppendLine("- **Querying**: `session.Query<T>().Where(...).ToListAsync()`");
    toc.AppendLine("- **Event Sourcing**: `session.Events.StartStream<T>(...)`");
    toc.AppendLine("- **GitHub**: https://github.com/microbians/AeroDB");
    toc.AppendLine("- **Docs**: https://docs.aerodb.io");

    await File.WriteAllTextAsync(tocPath, header + toc + footer);
    Console.WriteLine($"  aerodb-llms.txt = {FormatBytes(new FileInfo(tocPath).Length)}");
}

// llms-index.json for AI tooling discoverability
var indexJson = $$"""
{
  "library": "AeroDB",
  "version": "0.0.8-alpha",
  "generatedAt": "{{DateTimeOffset.UtcNow:O}}",
  "documentation": [
    { "file": "aerodb-llms-getting-started.txt", "topics": ["install", "configuration", "quickstart"] },
    { "file": "aerodb-llms-concepts.txt", "topics": ["architecture", "documents", "events", "projections", "tenancy"] },
    { "file": "aerodb-llms-guides.txt", "topics": ["crud", "linq", "surrealql", "event-sourcing", "live-queries", "recipes"] },
    { "file": "aerodb-llms-examples.txt", "topics": ["console", "aspnet", "blazor", "graph", "timeseries", "fulltext-search"] },
    { "file": "aerodb-llms-api.txt", "topics": ["classes", "methods", "interfaces", "enums", "properties"] },
    { "file": "aerodb-llms-advanced.txt", "topics": ["performance", "schema", "search", "serialization", "snowflake"] },
    { "file": "aerodb-llms-full.txt", "topics": ["all"] }
  ]
}
""";
var indexPath = Path.Combine(outDir, "llms-index.json");
await File.WriteAllTextAsync(indexPath, indexJson);
Console.WriteLine($"  llms-index.json = {FormatBytes(new FileInfo(indexPath).Length)}");

Console.WriteLine();
Console.WriteLine($"Done. Output dir: {Path.GetFullPath(outDir)}");

return;

// ── Deduplication (only for split files, not full) ────────────

static string DeduplicateCodeBlocks(string content, HashSet<string> seen)
{
    var sb = new StringBuilder();
    var inBlock = false;
    var currentBlock = new StringBuilder();

    foreach (var line in content.Split('\n'))
    {
        var trimmed = line.TrimStart();
        if (!inBlock && trimmed.StartsWith("```"))
        {
            inBlock = true;
            currentBlock.Clear();
            currentBlock.AppendLine(line);
        }
        else if (inBlock && trimmed == "```")
        {
            currentBlock.AppendLine(line);
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(currentBlock.ToString())));
            if (seen.Add(hash))
                sb.Append(currentBlock);
            inBlock = false;
        }
        else if (inBlock)
        {
            currentBlock.AppendLine(line);
        }
        else
        {
            sb.AppendLine(line);
        }
    }
    return sb.ToString();
}
