namespace Dali;

/// <summary>
/// Defines a SurrealDB index (simple, unique, composite, full-text, or vector).
/// </summary>
public class IndexDefinition
{
    public string Name { get; set; } = "";
    public string[] Columns { get; set; } = [];
    public bool IsUnique { get; set; }

    // --- Search index properties ---
    /// <summary>
    /// The index type (Standard, FullText, or Vector).
    /// </summary>
    public IndexType Type { get; set; } = IndexType.Standard;

    /// <summary>
    /// Full-text analyzer name (e.g. "simple"). Only used when <see cref="Type"/> is <see cref="IndexType.FullText"/>.
    /// </summary>
    public string? Analyzer { get; set; }

    /// <summary>
    /// BM25 scoring parameters (k1, b). Only used when <see cref="Type"/> is <see cref="IndexType.FullText"/>.
    /// </summary>
    public (double K1, double B)? Bm25 { get; set; }

    /// <summary>
    /// Vector dimension. Only used when <see cref="Type"/> is <see cref="IndexType.Hnsw"/> or <see cref="IndexType.Mtree"/>.
    /// </summary>
    public int? VectorDimension { get; set; }

    /// <summary>
    /// Distance function (e.g. "COSINE", "EUCLIDEAN", "MANHATTAN"). Only used when <see cref="Type"/> is <see cref="IndexType.Hnsw"/> or <see cref="IndexType.Mtree"/>.
    /// </summary>
    public string? VectorDistance { get; set; }
}

/// <summary>
/// The type of SurrealDB index to create.
/// </summary>
public enum IndexType
{
    /// <summary>
    /// Standard btree index — DEFINE INDEX ... COLUMNS ...
    /// </summary>
    Standard,

    /// <summary>
    /// Full-text search index — DEFINE INDEX ... FIELDS ... FULLTEXT ANALYZER ...
    /// </summary>
    FullText,

    /// <summary>
    /// HNSW vector index — DEFINE INDEX ... FIELDS ... HNSW DIMENSION ... DIST ...
    /// Approximate nearest-neighbor search using hierarchical navigable small world graphs.
    /// Best for large datasets where speed matters more than exact results.
    /// </summary>
    Hnsw,

    /// <summary>
    /// MTREE vector index — DEFINE INDEX ... FIELDS ... MTREE DIMENSION ... DIST ...
    /// Supports exact and approximate nearest-neighbor search.
    /// Best for smaller datasets or when exact results are required.
    /// Supports distance functions beyond cosine (Minkowski, Hamming, Jaccard).
    /// </summary>
    Mtree
}

/// <summary>
/// Configures an index being built via the fluent <see cref="DocumentMapping{T}"/> API.
/// </summary>
public class IndexOptions
{
    internal IndexOptions(IndexDefinition definition) => Definition = definition;
    internal IndexDefinition Definition { get; }

    public void IsUnique() => Definition.IsUnique = true;
    public void WithName(string name) => Definition.Name = name;
}
