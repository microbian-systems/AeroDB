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

    /// <summary>Search highlight for matched terms. Maps to <c>search::highlight(prefix, suffix, field)</c>.</summary>
    public static string Highlight(string prefix, string suffix, string field) => throw new NotSupportedException("SurrealFunctions.Highlight can only be used inside a LINQ expression.");

    /// <summary>Search offsets for matched terms. Maps to <c>search::offsets(field)</c>.</summary>
    public static string Offsets(string field) => throw new NotSupportedException("SurrealFunctions.Offsets can only be used inside a LINQ expression.");

    /// <summary>Analyze text with a specific analyzer. Maps to <c>search::analyze(text, analyzer)</c>.</summary>
    public static string Analyze(string text, string analyzer) => throw new NotSupportedException("SurrealFunctions.Analyze can only be used inside a LINQ expression.");

    /// <summary>Euclidean distance between two vectors. Maps to <c>vector::distance::euclidean(a, b)</c>.</summary>
    public static double VectorDistanceEuclidean(float[] a, float[] b) => throw new NotSupportedException("SurrealFunctions.VectorDistanceEuclidean can only be used inside a LINQ expression.");

    /// <summary>Manhattan distance between two vectors. Maps to <c>vector::distance::manhattan(a, b)</c>.</summary>
    public static double VectorDistanceManhattan(float[] a, float[] b) => throw new NotSupportedException("SurrealFunctions.VectorDistanceManhattan can only be used inside a LINQ expression.");
}
