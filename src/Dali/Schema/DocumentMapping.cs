using System.Linq.Expressions;
using System.Reflection;

namespace Dali;

/// <summary>
/// Non-generic base for storing document mappings in <see cref="SchemaOptions.Mappings"/>.
/// Public because <see cref="DocumentMapping{T}"/> inherits from it.
/// </summary>
public abstract class DocumentMapping
{
    internal abstract Type EntityType { get; }
    internal abstract List<IndexDefinition> Indices { get; }
    internal abstract bool IsMultiTenanted { get; }
    internal abstract SchemaMode SchemaModeType { get; }
}

public enum SchemaMode
{
    /// <summary>
    /// SCHEMAFULL — only explicitly defined fields are permitted; extra fields are rejected.
    /// </summary>
    Strict,

    /// <summary>
    /// SCHEMALESS — fields are typed/validated if defined, but extra fields are allowed.
    /// </summary>
    Flexible
}

/// <summary>
/// Fluent API for document-level schema configuration (indices, tenancy policy, etc.).
/// Accessed via <c>StoreOptions.Schema.For&lt;T&gt;()</c>.
/// </summary>
public class DocumentMapping<T> : DocumentMapping
    where T : SurrealDb.Net.Models.Record
{
    internal override Type EntityType => typeof(T);
    internal override List<IndexDefinition> Indices { get; } = [];
    private bool _isMultiTenanted;
    internal override bool IsMultiTenanted => _isMultiTenanted;
    private SchemaMode _schemaModeType = SchemaMode.Strict;
    internal override SchemaMode SchemaModeType => _schemaModeType;

    /// <summary>
    /// Sets the schema mode for this document type (SCHEMAFULL vs SCHEMALESS).
    /// Default is <see cref="Dali.SchemaMode.Strict"/> (SCHEMAFULL).
    /// </summary>
    public DocumentMapping<T> SetSchemaMode(Dali.SchemaMode mode)
    {
        _schemaModeType = mode;
        return this;
    }

    /// <summary>
    /// Defines a simple index on the specified property.
    /// </summary>
    public DocumentMapping<T> Index<TProp>(Expression<Func<T, TProp>> property, Action<IndexOptions>? configure = null)
    {
        var member = ExtractMember(property);
        var idx = new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"idx_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            IsUnique = false
        };
        configure?.Invoke(new IndexOptions(idx));
        Indices.Add(idx);
        return this;
    }

    /// <summary>
    /// Defines a unique index on the specified property.
    /// </summary>
    public DocumentMapping<T> UniqueIndex<TProp>(Expression<Func<T, TProp>> property)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"uidx_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            IsUnique = true
        });
        return this;
    }

    /// <summary>
    /// Defines a composite index on the specified properties.
    /// </summary>
    public DocumentMapping<T> CompositeIndex(params Expression<Func<T, object>>[] properties)
    {
        var columns = properties.Select(p => ExtractMember(p).Name).ToArray();
        Indices.Add(new IndexDefinition
        {
            Columns = columns,
            Name = $"idx_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(c => Snake(c)))}",
            IsUnique = false
        });
        return this;
    }

    /// <summary>
    /// Defines a composite unique index.
    /// </summary>
    public DocumentMapping<T> UniqueCompositeIndex(params Expression<Func<T, object>>[] properties)
    {
        var columns = properties.Select(p => ExtractMember(p).Name).ToArray();
        Indices.Add(new IndexDefinition
        {
            Columns = columns,
            Name = $"uidx_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(c => Snake(c)))}",
            IsUnique = true
        });
        return this;
    }

    /// <summary>
    /// Defines a full-text search index on the specified property.
    /// Requires an analyzer to be defined via <c>SchemaOptions.DefineAnalyzer()</c> or pre-existing in the database.
    /// </summary>
    /// <param name="property">The property to index.</param>
    /// <param name="analyzer">The SurrealDB analyzer name (e.g. "simple").</param>
    /// <param name="bm25">Optional BM25 scoring parameters (k1, b).</param>
    public DocumentMapping<T> FullTextIndex<TProp>(
        Expression<Func<T, TProp>> property,
        string analyzer,
        (double K1, double B)? bm25 = null)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"ft_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.FullText,
            Analyzer = analyzer,
            Bm25 = bm25
        });
        return this;
    }

    /// <summary>
    /// Defines an HNSW vector search index on the specified property.
    /// Uses approximate nearest-neighbor for fast vector similarity queries.
    /// </summary>
    /// <param name="property">The property storing the vector embedding.</param>
    /// <param name="dimension">The dimensionality of the vector (e.g. 1536 for OpenAI ada-002).</param>
    /// <param name="distance">The distance function: COSINE (default), EUCLIDEAN, or MANHATTAN.</param>
    public DocumentMapping<T> VectorIndex<TProp>(
        Expression<Func<T, TProp>> property,
        int dimension,
        string distance = Search.Distance.Cosine)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"hnsw_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.Vector,
            VectorDimension = dimension,
            VectorDistance = distance
        });
        return this;
    }

    /// <summary>
    /// Convenience: creates both full-text and HNSW indexes for hybrid search.
    /// Sets up full-text indexes on each of the specified text fields.
    /// </summary>
    /// <param name="textFields">Pairs of (field name, weight) for full-text search fields.</param>
    /// <param name="vectorField">The property storing the vector embedding.</param>
    /// <param name="dimension">The dimensionality of the vector.</param>
    /// <param name="analyzer">The SurrealDB analyzer name for full-text fields.</param>
    /// <param name="distance">The distance function for HNSW.</param>
    public DocumentMapping<T> HybridSearch(
        IReadOnlyList<(string FieldName, double Weight)> textFields,
        Expression<Func<T, object>> vectorField,
        int dimension,
        string analyzer,
        string distance = Search.Distance.Cosine)
    {
        foreach (var (fieldName, _) in textFields)
        {
            Indices.Add(new IndexDefinition
            {
                Columns = [fieldName],
                Name = $"ft_{Snake(typeof(T).Name)}_{Snake(fieldName)}",
                Type = IndexType.FullText,
                Analyzer = analyzer
            });
        }

        var vecMember = ExtractMember(vectorField);
        Indices.Add(new IndexDefinition
        {
            Columns = [vecMember.Name],
            Name = $"hnsw_{Snake(typeof(T).Name)}_{Snake(vecMember.Name)}",
            Type = IndexType.Vector,
            VectorDimension = dimension,
            VectorDistance = distance
        });
        return this;
    }

    /// <summary>
    /// Marks this document type as multi-tenanted (tenant_id filter applied).
    /// </summary>
    public DocumentMapping<T> MultiTenanted()
    {
        _isMultiTenanted = true;
        return this;
    }

    private static MemberInfo ExtractMember<TProp>(Expression<Func<T, TProp>> expression)
    {
        return expression.Body switch
        {
            MemberExpression me => me.Member,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: MemberExpression me } => me.Member,
            _ => throw new ArgumentException("Expression must refer to a property or field.")
        };
    }

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }
}
