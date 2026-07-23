using System.Linq.Expressions;
using SurrealDb.Net;

namespace AeroDB.Sable;

public static class SearchExtensions
{
    /// <summary>
    /// Full-text search using the @@ operator. Requires a FULLTEXT ANALYZER index on the field.
    /// Supports weighted multi-field queries (e.g., title: 25, body: 10).
    /// </summary>
    public static async Task<List<T>> MatchTextAsync<T>(
        this ISableQueryable<T> source,
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

        return await new AeroDBSearchQuery<T>(provider)
            .MatchText(converted, query)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-field full-text search. Convenience overload for the most common case.
    /// </summary>
    public static Task<List<T>> MatchTextAsync<T>(
        this ISableQueryable<T> source,
        Expression<Func<T, string>> fieldSelector,
        string query,
        int limit = 30,
        CancellationToken ct = default) where T : class
        => MatchTextAsync(source, [(fieldSelector, 1.0)], query, limit, ct);

    /// <summary>
    /// KNN vector search using the &lt;||&gt; operator. Requires an HNSW index on the field.
    /// </summary>
    public static async Task<List<T>> MatchKnnAsync<T>(
        this ISableQueryable<T> source,
        Expression<Func<T, float[]>> fieldSelector,
        float[] queryVector,
        int limit = 30,
        int candidates = 100,
        CancellationToken ct = default) where T : class
    {
        if (source.Provider is not SurrealQueryProvider provider)
            return [];

        return await new AeroDBSearchQuery<T>(provider)
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
        this ISableQueryable<T> source,
        HybridSearchConfig config,
        CancellationToken ct = default) where T : class
    {
        if (source.Provider is not SurrealQueryProvider provider)
            return [];

        if (config.TextFields.Count == 0 || config.QueryVector.Length == 0)
            return [];

        var query = new AeroDBSearchQuery<T>(provider);

        foreach (var (fieldName, weight) in config.TextFields)
            query.MatchTextField(fieldName, weight, config.Query);

        return await query
            .WithVectorField(config.VectorField, config.QueryVector)
            .Candidates(config.VectorCandidates)
            .FuseAsync(config.RrfK, config.RrfLimit, ct)
            .ConfigureAwait(false);
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
