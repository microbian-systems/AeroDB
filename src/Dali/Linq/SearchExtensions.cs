using System.Linq.Expressions;
using Dali.Metadata;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Dali;

public static class SearchExtensions
{
    /// <summary>
    /// Full-text search using the @@ operator. Requires a FULLTEXT ANALYZER index on the field.
    /// Supports weighted multi-field queries (e.g., title: 25, body: 10).
    /// </summary>
    public static async Task<List<T>> MatchTextAsync<T>(
        this ISurrealDbQueryable<T> source,
        IReadOnlyList<(Expression<Func<T, string>> FieldSelector, double Weight)> fields,
        string query,
        int limit = 30,
        CancellationToken ct = default) where T : class
    {
        if (source.Provider is not SurrealQueryProvider provider)
            return [];

        if (fields.Count == 0) return [];

        // Convert Expression<Func<T,string>> to Expression<Func<T,object>>
        var converted = fields.Select(f =>
            ((Expression<Func<T, object>>)Expression.Lambda(
                Expression.Convert(f.FieldSelector.Body, typeof(object)),
                f.FieldSelector.Parameters),
             f.Weight)).ToArray();

        return await new DaliSearchQuery<T>(provider)
            .MatchText(converted, query)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-field full-text search. Convenience overload for the most common case.
    /// </summary>
    public static Task<List<T>> MatchTextAsync<T>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, string>> fieldSelector,
        string query,
        int limit = 30,
        CancellationToken ct = default) where T : class
        => MatchTextAsync(source, [(fieldSelector, 1.0)], query, limit, ct);

    /// <summary>
    /// KNN vector search using the &lt;||&gt; operator. Requires an HNSW index on the field.
    /// </summary>
    public static async Task<List<T>> MatchKnnAsync<T>(
        this ISurrealDbQueryable<T> source,
        Expression<Func<T, float[]>> fieldSelector,
        float[] queryVector,
        int limit = 30,
        int candidates = 100,
        CancellationToken ct = default) where T : class
    {
        if (source.Provider is not SurrealQueryProvider provider)
            return [];

        return await new DaliSearchQuery<T>(provider)
            .WithVector(fieldSelector, queryVector)
            .Take(limit)
            .Candidates(candidates)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hybrid search combining full-text + vector results via Reciprocal Rank Fusion.
    /// Requires FULLTEXT + HNSW indexes. Fuses results using search::rrf().
    /// </summary>
    public static async Task<List<T>> HybridSearchAsync<T>(
        this ISurrealDbQueryable<T> source,
        HybridSearchConfig config,
        CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var rrfK = config.RrfK > 0 ? config.RrfK : 60;
        var rrfLimit = config.RrfLimit > 0 ? config.RrfLimit : 80;

        // Build full-text match part
        var ftWhereParts = new List<string>();
        var ftScoreParts = new List<string>();
        for (int i = 0; i < config.TextFields.Count; i++)
        {
            var (fieldName, weight) = config.TextFields[i];
            ftWhereParts.Add($"{fieldName} @{i}@ '{config.Query.Replace("'", "\\'")}'");
            ftScoreParts.Add($"(search::score({i}) * {weight})");
        }

        var ftWhere = string.Join(" OR ", ftWhereParts);
        var ftScoreExpr = ftScoreParts.Count == 1 ? $"search::score(0)" : string.Join(" + ", ftScoreParts);

        // Build vector search part
        var vecStr = "[" + string.Join(", ", config.QueryVector.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

        // Combined SurrealQL with LET variables and RRF
        var surql = $@"
LET $ft = (
    SELECT *, {ftScoreExpr} AS _ft_score
    FROM `{table}`
    WHERE {ftWhere}
    ORDER BY _ft_score DESC
    LIMIT {rrfLimit}
);
LET $vs = (
    SELECT *, vector::distance::knn() AS _distance
    FROM `{table}`
    WHERE {config.VectorField} <|{rrfLimit},{config.VectorCandidates}|> {vecStr}
    ORDER BY _distance ASC
    LIMIT {rrfLimit}
);
RETURN search::rrf([$ft, $vs], {rrfK}, {rrfLimit});";

        return await ExecuteSearchAsync<T>(source, surql, ct).ConfigureAwait(false);
    }

    private static async Task<List<T>> ExecuteSearchAsync<T>(
        ISurrealDbQueryable<T> source, string surql, CancellationToken ct) where T : class
    {
        if (source.Provider is SurrealQueryProvider surrealProvider)
        {
            var response = await surrealProvider.Session.RawQuery(surql, null, ct).ConfigureAwait(false);
            if (!response.HasErrors && response.Count > 0)
            {
                var raw = response.GetValue<List<T>>(0);
                if (raw is not null) return raw;
            }
        }
        return [];
    }


}

/// <summary>
/// Configuration for hybrid search combining full-text and vector results.
/// </summary>
public sealed class HybridSearchConfig
{
    /// <summary>The search query string for full-text matching.</summary>
    public string Query { get; set; } = "";

    /// <summary>The embedding/query vector for vector search.</summary>
    public float[] QueryVector { get; set; } = [];

    /// <summary>Field name for the vector/embedding index.</summary>
    public string VectorField { get; set; } = "Embedding";

    /// <summary>Named fields with weights for full-text search (e.g., title=25, description=10).</summary>
    public IReadOnlyList<(string FieldName, double Weight)> TextFields { get; set; } = [];

    /// <summary>KNN candidates to explore (higher = more accurate, slower). Default 100.</summary>
    public int VectorCandidates { get; set; } = 100;

    /// <summary>RRF smoothness constant (k). Default 60.</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Maximum candidates for RRF fusion. Default 80.</summary>
    public int RrfLimit { get; set; } = 80;
}
