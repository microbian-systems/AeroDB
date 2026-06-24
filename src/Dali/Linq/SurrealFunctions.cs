namespace Dali;

/// <summary>
/// SurrealQL search and vector functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// Use <see cref="SearchExtensions.MatchTextAsync{T}"/> and <see cref="SearchExtensions.MatchKnnAsync{T}"/> for query execution.
/// </summary>
public static class SurrealFunctions
{
    /// <summary>BM25 relevance score for the nth full-text match operator (@n@). Only valid inside a MatchText query.</summary>
    public static double Score(int n) => throw new NotSupportedException("SurrealFunctions.Score can only be used inside a LINQ expression.");

    /// <summary>KNN distance inside a MatchKnn query scope. Only valid with the &lt;||&gt; operator.</summary>
    public static double VectorDistanceKnn() => throw new NotSupportedException("SurrealFunctions.VectorDistanceKnn can only be used inside a LINQ expression.");

    /// <summary>Cosine similarity between two vectors. Brute-force; prefer HNSW index for scale.</summary>
    public static double VectorSimilarityCosine(float[] a, float[] b) => throw new NotSupportedException("SurrealFunctions.VectorSimilarityCosine can only be used inside a LINQ expression.");
}
