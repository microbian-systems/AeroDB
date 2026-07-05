using AngleSharp;
using AngleSharp.Dom;
using System.Text.RegularExpressions;

/// <summary>
/// Fetches and parses a random Wikipedia article using AngleSharp.
/// </summary>
public static class WikipediaFetcher
{
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "AeroDB-Demo/1.0 (https://github.com/microbian-systems/AeroDB)" } }
    };

    private static readonly IBrowsingContext _context = BrowsingContext.New(
        Configuration.Default.WithDefaultLoader());

    /// <summary>A fetched Wikipedia article.</summary>
    public sealed record Article(string Title, string Url, string Content);

    /// <summary>
    /// Fetch a random Wikipedia article. Follows the redirect from
    /// https://en.wikipedia.org/wiki/Special:Random to the actual article,
    /// then extracts the title and body text.
    /// </summary>
    public static async Task<Article> FetchRandomAsync(CancellationToken ct = default)
    {
        // Wikipedia Special:Random returns a 302 redirect to the actual article
        using var response = await _http.GetAsync(
            "https://en.wikipedia.org/wiki/Special:Random",
            HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage!.RequestUri!.ToString();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await _context.OpenAsync(req => req.Content(html), ct);

        var title = ExtractTitle(doc);
        var bodyText = ExtractBodyText(doc);

        if (string.IsNullOrWhiteSpace(bodyText))
            throw new InvalidOperationException(
                $"Failed to extract body text from {finalUrl}");

        return new Article(title, finalUrl, CleanText(bodyText));
    }

    /// <summary>
    /// Fetch N unique random articles in parallel.
    /// Duplicate URLs are filtered out (Wikipedia Special:Random
    /// occasionally returns the same page).
    /// </summary>
    public static async Task<Article[]> FetchRandomBatchAsync(
        int count, CancellationToken ct = default)
    {
        // Fire all requests in parallel
        var tasks = Enumerable.Range(0, count)
            .Select(_ => FetchRandomSafeAsync(ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var unique = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase);

        foreach (var article in results)
        {
            if (article is null) continue;
            unique.TryAdd(article.Url, article);
        }

        if (unique.Count < count)
            Console.WriteLine($"  [Wikipedia] Got {unique.Count} unique articles " +
                $"(requested {count}, {count - unique.Count} duplicates)");

        return unique.Values.ToArray();
    }

    private static async Task<Article?> FetchRandomSafeAsync(CancellationToken ct)
    {
        try
        {
            return await FetchRandomAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Wikipedia] Fetch failed: {ex.Message}");
            return null;
        }
    }

    // ────────────────────────────────────────────────────
    //  HTML extraction
    // ────────────────────────────────────────────────────

    private static string ExtractTitle(IDocument doc)
    {
        // <h1 id="firstHeading"> for the page title
        var h1 = doc.QuerySelector("h1#firstHeading");
        if (h1 is not null) return h1.TextContent.Trim();

        // Fallback to <title>
        var title = doc.QuerySelector("title");
        if (title is not null)
        {
            var text = title.TextContent.Replace(" - Wikipedia", "").Trim();
            return text;
        }

        return "Unknown";
    }

    private static string ExtractBodyText(IDocument doc)
    {
        // Wikipedia content lives in #mw-content-text > .mw-content-ltr > p
        // We also check .mw-parser-output as a fallback
        var contentDiv = doc.QuerySelector("#mw-content-text");
        if (contentDiv is null)
            return string.Empty;

        var paragraphs = contentDiv.QuerySelectorAll("p");
        if (paragraphs.Length == 0)
            return string.Empty;

        var texts = new List<string>();
        foreach (var p in paragraphs)
        {
            var text = p.TextContent.Trim();
            if (text.Length > 20) // Skip tiny paragraphs (captions, nav)
                texts.Add(text);
        }

        return string.Join("\n\n", texts);
    }

    /// <summary>Remove citation brackets, extra whitespace, and control chars.</summary>
    private static string CleanText(string text)
    {
        // Remove Wikipedia citation brackets: [1], [12], [citation needed], etc.
        text = Regex.Replace(text, @"\[\d+\]", "");
        text = Regex.Replace(text, @"\[citation needed\]", "",
            RegexOptions.IgnoreCase);

        // Remove multiple newlines
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        // Remove excessive whitespace
        text = Regex.Replace(text, @"[ \t]{2,}", " ");

        return text.Trim();
    }
}
