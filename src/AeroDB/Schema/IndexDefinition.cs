namespace AeroDB;

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
    /// Vector dimension. Only used when <see cref="Type"/> is <see cref="IndexType.Hnsw"/> or <see cref="IndexType.Diskann"/>.
    /// </summary>
    public int? VectorDimension { get; set; }

    /// <summary>
    /// Distance function (e.g. "COSINE", "EUCLIDEAN", "MANHATTAN").
    /// Only used when <see cref="Type"/> is <see cref="IndexType.Hnsw"/> or <see cref="IndexType.Diskann"/>.
    /// </summary>
    public string? VectorDistance { get; set; }

    /// <summary>
    /// Element type encoding for vector elements. Used by DISKANN index.
    /// Valid values: F32 (default), F16, I8, U8.
    /// HNSW uses F64 by default and accepts F64, F32, I64, I32, I16.
    /// </summary>
    public string? VectorElementType { get; set; }

    /// <summary>DiskANN target maximum graph degree (default 64).</summary>
    public int? DiskannDegree { get; set; }

    /// <summary>DiskANN construction search-list size (default 100).</summary>
    public int? DiskannLBuild { get; set; }

    /// <summary>DiskANN pruning parameter (default 1.2).</summary>
    public double? DiskannAlpha { get; set; }

    /// <summary>Whether to use hash-stabilised vector–document keys for DiskANN.</summary>
    public bool HasHashedVector { get; set; }

    /// <summary>Options for computed/expression-based indexes.</summary>
    public ComputedIndexOptions? ComputedOptions { get; set; }
}

/// <summary>
/// Options for computed/expression-based indexes. Controls index method,
/// casing, sort order, and optional predicate filtering.
/// </summary>
public class ComputedIndexOptions
{
    public enum IndexMethod { BTree, Unique, Search }
    public enum IndexCasing { Default, Lower, Upper }
    public enum SortOrder { Asc, Desc }

    /// <summary>Index method. Default is BTree.</summary>
    public IndexMethod Method { get; set; } = IndexMethod.BTree;

    /// <summary>Field casing. Default is platform-default.</summary>
    public IndexCasing Casing { get; set; } = IndexCasing.Default;

    /// <summary>Sort order. Default is Ascending.</summary>
    public SortOrder? Order { get; set; }

    /// <summary>
    /// Optional predicate for partial indexes (e.g., "WHERE published = true").
    /// Only applied when the target database supports it.
    /// </summary>
    public string? Predicate { get; set; }
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
    /// DISKANN vector index — DEFINE INDEX ... FIELDS ... DISKANN DIMENSION ... TYPE ... DIST ...
    /// Disk-based approximate nearest-neighbour search for very large embedding sets.
    /// Uses the same &lt;|K, EF|&gt; query operator as HNSW. Available since SurrealDB 3.1.
    /// Best when the working set exceeds available RAM (vs HNSW for in-memory).
    /// </summary>
    Diskann,

    /// <summary>
    /// Geo-spatial marker index. Uses a standard btree under the hood
    /// (SurrealDB has no native spatial index). The marker enables informational
    /// logging during index creation to document intent.
    /// </summary>
    Geo
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
