using System.Linq.Expressions;
using System.Reflection;
using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Non-generic base for storing document mappings in <see cref="SchemaOptions.Mappings"/>.
/// Public because <see cref="DocumentMapping{T}"/> inherits from it.
/// </summary>
public abstract class DocumentMapping
{
    internal abstract Type EntityType { get; }

    /// <summary>
    /// The CLR type mapped by this document mapping.
    /// </summary>
    public Type DocumentType => EntityType;

    internal abstract List<IndexDefinition> Indices { get; }
    internal abstract bool IsMultiTenanted { get; }
    internal abstract SchemaMode SchemaModeType { get; }
    internal abstract string? SchemaName { get; }

    /// <summary>Custom field definitions for this document type. Overridden in generic subclass.</summary>
    internal virtual IReadOnlyList<FieldDefinition> GetFieldDefinitions() => [];

    /// <summary>Multi-tenancy style for this document type.</summary>
    public TenancyStyle TenancyStyle { get; set; }

    /// <summary>When true, this document type supports soft-delete (ISoftDeleted).</summary>
    public bool SoftDeleted { get; set; }

    /// <summary>When true, optimistic concurrency is enforced for this document type.</summary>
    public bool UseOptimisticConcurrency { get; set; }

    /// <summary>The configured identity (primary key) property name. Set via <c>Schema.For&lt;T&gt;().Identity(...)</c>.</summary>
    internal string? IdentityProperty { get; set; }
}

/// <summary>Controls the SurrealDB table schema mode. <c>Schemaless</c> (Flexible) allows any fields; <c>Schemafull</c> (Strict) enforces a strict field definition.</summary>
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
{
    internal override Type EntityType => typeof(T);
    internal override List<IndexDefinition> Indices { get; } = [];
    private bool _isMultiTenanted;
    internal override bool IsMultiTenanted => _isMultiTenanted;
    private SchemaMode _schemaModeType = SchemaMode.Strict;
    internal override SchemaMode SchemaModeType => _schemaModeType;
    private string? _schemaName;
    internal override string? SchemaName => _schemaName;

    private readonly List<Type> _subClasses = [];
    private readonly HashSet<string> _ignoredIndexes = [];
    private readonly List<ForeignKeyDefinition> _foreignKeys = [];
    private readonly List<FieldDefinition> _fieldDefinitions = [];

    internal DocumentMapping()
    {
        ValidateDocumentType<T>();
    }

    /// <summary>Register a derived type for polymorphic querying.</summary>
    public DocumentMapping<T> AddSubClass<TDerived>() where TDerived : T
    {
        _subClasses.Add(typeof(TDerived));
        return this;
    }

    /// <summary>Registered derived types for this document type.</summary>
    public IReadOnlyList<Type> SubClasses => _subClasses;

    /// <summary>Prevent the schema manager from creating a specific index.</summary>
    public DocumentMapping<T> IgnoreIndex(string indexName)
    {
        _ignoredIndexes.Add(indexName);
        return this;
    }

    /// <summary>Index names that should not be created by EnsureSchema.</summary>
    public IReadOnlyCollection<string> IgnoredIndexes => _ignoredIndexes;

    /// <summary>
    /// Declare a foreign key relationship for informational/validation purposes.
    /// In SurrealDB, graph edges replace FK cascades — this metadata is stored
    /// for tooling and documentation only.
    /// </summary>
    public DocumentMapping<T> ForeignKey<TChild>(Expression<Func<T, object>> property, Action<ForeignKeyDefinition>? configure = null)
    {
        var member = ExtractMemberFromBody(property.Body);
        var fk = new ForeignKeyDefinition(member.Name, typeof(TChild));
        configure?.Invoke(fk);
        _foreignKeys.Add(fk);
        return this;
    }

    /// <summary>Registered foreign key definitions.</summary>
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys => _foreignKeys;

    /// <summary>
    /// Define a field with optional type, default, assertion, and permissions.
    /// These are emitted as additional DEFINE FIELD statements by the schema manager.
    /// </summary>
    public DocumentMapping<T> Field(string fieldName, Action<FieldDefinition>? configure = null)
    {
        var def = new FieldDefinition { FieldName = fieldName };
        configure?.Invoke(def);
        _fieldDefinitions.Add(def);
        return this;
    }

    /// <summary>Custom field definitions for this document type.</summary>
    public IReadOnlyList<FieldDefinition> FieldDefinitions => _fieldDefinitions;
    internal override IReadOnlyList<FieldDefinition> GetFieldDefinitions() => _fieldDefinitions;

    /// <summary>
    /// Defines a computed/expression-based index with configurable options
    /// (index method, casing, sort order, predicate).
    /// </summary>
    public DocumentMapping<T> ComputedIndex(Expression<Func<T, object>> expression, Action<ComputedIndexOptions> configure)
    {
        var opts = new ComputedIndexOptions();
        configure(opts);

        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } ue)
            body = ue.Operand;

        var columns = body switch
        {
            NewExpression ne when IsAnonymousType(ne.Type) =>
                ne.Arguments.Select(ExtractMemberFromArgument).Select(m => m.Name).ToArray(),
            _ => [ExtractMemberFromBody(body).Name]
        };

        var idxName = $"cidx_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(Snake))}";
        var idx = new IndexDefinition
        {
            Columns = columns,
            Name = idxName,
            Type = IndexType.Standard,
            ComputedOptions = opts
        };
        Indices.Add(idx);
        return this;
    }

    internal static void ValidateDocumentType<TDocument>()
    {
        // POCOs are now supported via Schema.For<T>().Identity(x => x.Id)
        // Validation is deferred to schema generation time
    }

    /// <summary>
    /// Sets the schema mode for this document type (SCHEMAFULL vs SCHEMALESS).
    /// Default is <see cref="Dali.SchemaMode.Strict"/> (SCHEMAFULL).
    /// </summary>
    public DocumentMapping<T> SetSchemaMode(Dali.SchemaMode mode)
    {
        _schemaModeType = mode;
        return this;
    }

    private static readonly HashSet<Type> SupportedIdentityTypes = new()
    {
        typeof(long), typeof(int), typeof(ulong), typeof(uint),
        typeof(string), typeof(Guid), typeof(byte), typeof(short),
        typeof(DateTime)
    };

    /// <summary>
    /// Designates the primary key property explicitly for documentation purposes.
    /// For <see cref="SurrealDb.Net.Models.IRecord"/> types, the Id property
    /// (<see cref="SurrealDb.Net.Models.RecordId"/>) is always the primary key.
    /// This method is present for API consistency and future extensibility.
    /// </summary>
    public DocumentMapping<T> Identity<TProp>(Expression<Func<T, TProp>> property)
    {
        var member = ExtractMember(property);
        var propType = typeof(TProp);
        if (!SupportedIdentityTypes.Contains(propType))
            throw new ArgumentException(
                $"Identity property type '{propType.Name}' is not supported. " +
                $"Supported types: long, int, ulong, uint, string, Guid, byte, short, DateTime.");
        IdentityProperty = member.Name;
        return this;
    }

    /// <summary>
    /// Maps this document type to a specific SurrealDB database (schema).
    /// When null (default), the entity uses the default database from <c>StoreOptions.Database</c>.
    /// </summary>
    /// <param name="schemaName">The SurrealDB database name, or null for the default.</param>
    public DocumentMapping<T> Schema(string? schemaName)
    {
        _schemaName = schemaName;
        return this;
    }

    /// <summary>
    /// Defines a simple index on the specified property.
    /// SurrealDB's default index type is btree, so this is equivalent to <see cref="BTreeIndex{TProp}"/>.
    /// </summary>
    public DocumentMapping<T> Index<TProp>(Expression<Func<T, TProp>> property, Action<IndexOptions>? configure = null)
    {
        // Delegate to the non-generic overload — wraps if value types need boxing
        var body = (Expression)property.Body;
        if (body.Type != typeof(object))
            body = Expression.Convert(body, typeof(object));
        var wrapped = Expression.Lambda<Func<T, object>>(body, property.Parameters);
        return Index(wrapped, configure);
    }

    /// <summary>
    /// Defines a computed index. Supports single-property and multi-property
    /// anonymous-type expressions (<c>x => new { x.FirstName, x.LastName }</c>).
    /// For strongly-typed single-property access, prefer <see cref="Index{TProp}"/>.
    /// </summary>
    public DocumentMapping<T> Index(Expression<Func<T, object>> expression, Action<IndexOptions>? configure = null)
    {
        // Strip outer Convert/ConvertChecked — the generic Index<TProp> overload
        // introduces it for reference types whose type != typeof(object) (including
        // anonymous types which are reference types but not object).
        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } ue)
            body = ue.Operand;

        var columns = body switch
        {
            NewExpression ne when IsAnonymousType(ne.Type) =>
                ne.Arguments.Select(ExtractMemberFromArgument).Select(m => m.Name).ToArray(),

            NewExpression =>
                throw new ArgumentException(
                    $"Multi-column index requires an anonymous type (new {{ ... }}). " +
                    $"Use Index<TProp>() for single columns, or CompositeIndex() with explicit property lambdas.", nameof(expression)),

            _ => [ExtractMemberFromBody(body).Name]
        };

        var idx = new IndexDefinition { Columns = columns, IsUnique = false, Type = IndexType.Standard };
        configure?.Invoke(new IndexOptions(idx));
        if (string.IsNullOrEmpty(idx.Name))
        {
            var prefix = idx.IsUnique ? "uidx" : "idx";
            idx.Name = $"{prefix}_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(Snake))}";
        }

        Indices.Add(idx);
        return this;
    }

    /// <summary>
    /// Defines a btree index on the specified property.
    /// Equivalent to <c>Index()</c> — SurrealDB's default index type is btree.
    /// This explicit alias exists for clarity when SurrealDB-native naming is preferred.
    /// </summary>
    public DocumentMapping<T> BTreeIndex<TProp>(Expression<Func<T, TProp>> property, Action<IndexOptions>? configure = null)
        => Index(property, configure);

    /// <summary>
    /// Defines a unique index on the specified property.
    /// </summary>
    public DocumentMapping<T> UniqueIndex<TProp>(Expression<Func<T, TProp>> property)
        => Index(property, c => c.IsUnique());

    /// <summary>
    /// Defines a composite index on the specified properties.
    /// Prefer <see cref="Index(Expression{Func{T, object}}, Action{IndexOptions}?)"/> with an anonymous type expression.
    /// </summary>
    [Obsolete("Use Index(x => new { x.Prop1, x.Prop2 }, configure) instead.")]
    public DocumentMapping<T> CompositeIndex(params Expression<Func<T, object>>[] properties)
    {
#pragma warning disable CS0618
        return CompositeIndex(null, properties);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Defines a composite index on the specified properties with configuration.
    /// Prefer <see cref="Index(Expression{Func{T, object}}, Action{IndexOptions}?)"/> with an anonymous type expression.
    /// </summary>
    [Obsolete("Use Index(x => new { x.Prop1, x.Prop2 }, configure) instead.")]
    public DocumentMapping<T> CompositeIndex(Action<IndexOptions>? configure, params Expression<Func<T, object>>[] properties)
    {
        var columns = properties.Select(p => ExtractMember(p).Name).ToArray();
        var idx = new IndexDefinition { Columns = columns, IsUnique = false };
        configure?.Invoke(new IndexOptions(idx));
        if (string.IsNullOrEmpty(idx.Name))
        {
            var prefix = idx.IsUnique ? "uidx" : "idx";
            idx.Name = $"{prefix}_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(Snake))}";
        }
        Indices.Add(idx);
        return this;
    }

    /// <summary>
    /// Defines a composite unique index.
    /// Prefer <see cref="Index(Expression{Func{T, object}}, Action{IndexOptions}?)"/> with <c>configure => configure.IsUnique()</c>.
    /// </summary>
    [Obsolete("Use Index(x => new { x.Prop1, x.Prop2 }, c => c.IsUnique()) instead.")]
    public DocumentMapping<T> UniqueCompositeIndex(params Expression<Func<T, object>>[] properties)
    {
#pragma warning disable CS0618
        return UniqueCompositeIndex(null, properties);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Defines a composite unique index with configuration.
    /// Prefer <see cref="Index(Expression{Func{T, object}}, Action{IndexOptions}?)"/> with <c>configure => configure.IsUnique()</c>.
    /// </summary>
    [Obsolete("Use Index(x => new { x.Prop1, x.Prop2 }, c => c.IsUnique()) instead.")]
    public DocumentMapping<T> UniqueCompositeIndex(Action<IndexOptions>? configure, params Expression<Func<T, object>>[] properties)
    {
        var columns = properties.Select(p => ExtractMember(p).Name).ToArray();
        var idx = new IndexDefinition { Columns = columns, IsUnique = true };
        configure?.Invoke(new IndexOptions(idx));
        if (string.IsNullOrEmpty(idx.Name))
        {
            idx.Name = $"uidx_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(Snake))}";
        }
        Indices.Add(idx);
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
    /// Defines a multi-field full-text search index on the specified properties.
    /// SurrealDB indexes all specified columns together in a single FTS index
    /// using <c>DEFINE INDEX ... FIELDS col1, col2, ... FULLTEXT ANALYZER ...</c>.
    /// </summary>
    /// <param name="analyzer">The SurrealDB analyzer name (e.g. "english").</param>
    /// <param name="property1">First property to include in the FTS index.</param>
    /// <param name="property2">Second property to include in the FTS index.</param>
    /// <param name="additionalProperties">Additional properties to include.</param>
    public DocumentMapping<T> FullTextIndex(
        string analyzer,
        Expression<Func<T, object>> property1,
        Expression<Func<T, object>> property2,
        params Expression<Func<T, object>>[] additionalProperties)
    {
        var columns = new List<string> { ExtractMember(property1).Name, ExtractMember(property2).Name };
        columns.AddRange(additionalProperties.Select(p => ExtractMember(p).Name));
        Indices.Add(new IndexDefinition
        {
            Columns = columns.ToArray(),
            Name = $"ft_{Snake(typeof(T).Name)}_{string.Join("_", columns.Select(Snake))}",
            Type = IndexType.FullText,
            Analyzer = analyzer
        });
        return this;
    }
    /// <summary>
    /// Uses the HNSW algorithm for fast approximate vector similarity queries.
    /// Best for large datasets where speed matters more than exact results.
    /// </summary>
    /// <param name="property">The property storing the vector embedding.</param>
    /// <param name="dimension">The dimensionality of the vector (e.g. 1536 for OpenAI ada-002).</param>
    /// <param name="distance">The distance function: COSINE (default), EUCLIDEAN, or MANHATTAN.</param>
    public DocumentMapping<T> HnswIndex<TProp>(
        Expression<Func<T, TProp>> property,
        int dimension,
        string distance = Search.Distance.Cosine)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"hnsw_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.Hnsw,
            VectorDimension = dimension,
            VectorDistance = distance
        });
        return this;
    }

    /// <summary>
    /// Defines an MTREE vector index on the specified property.
    /// MTREE supports both exact and approximate nearest-neighbor search.
    /// Supports distance functions beyond cosine (Minkowski, Hamming, Jaccard).
    /// Best for smaller datasets or when exact results are required.
    /// </summary>
    /// <param name="property">The property storing the vector embedding.</param>
    /// <param name="dimension">The dimensionality of the vector.</param>
    /// <param name="distance">The distance function: COSINE (default), EUCLIDEAN, MANHATTAN, MINKOWSKI, HAMMING, or JACCARD.</param>
    public DocumentMapping<T> MtreeIndex<TProp>(
        Expression<Func<T, TProp>> property,
        int dimension,
        string distance = Search.Distance.Cosine)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"mtree_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.Mtree,
            VectorDimension = dimension,
            VectorDistance = distance
        });
        return this;
    }

    /// <summary>
    /// Defines a DISKANN vector index on the specified property.
    /// DiskANN is a disk-based approximate nearest-neighbour index for very large
    /// embedding sets. Uses the same &lt;|K, EF|&gt; query operator as HNSW.
    /// Available since SurrealDB 3.1. Best when the working set exceeds available RAM.
    /// </summary>
    /// <param name="property">The property storing the vector embedding.</param>
    /// <param name="dimension">The dimensionality of the vector.</param>
    /// <param name="vectorType">Element type: F32 (default), F16, I8, or U8.</param>
    /// <param name="distance">The distance function: COSINE (default), EUCLIDEAN, INNER_PRODUCT, or COSINE_NORMALIZED.</param>
    /// <param name="degree">DiskANN graph degree (default 64). Higher values improve accuracy at cost of memory.</param>
    /// <param name="lBuild">Construction search-list size (default 100).</param>
    /// <param name="alpha">DiskANN pruning parameter (default 1.2).</param>
    /// <param name="hashedVector">Whether to use hash-stabilized vector keys.</param>
    public DocumentMapping<T> DiskannIndex<TProp>(
        Expression<Func<T, TProp>> property,
        int dimension,
        string vectorType = "F32",
        string distance = Search.Distance.Cosine,
        int? degree = null,
        int? lBuild = null,
        double? alpha = null,
        bool hashedVector = false)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"diskann_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.Diskann,
            VectorDimension = dimension,
            VectorDistance = distance,
            VectorElementType = vectorType,
            DiskannDegree = degree,
            DiskannLBuild = lBuild,
            DiskannAlpha = alpha,
            HasHashedVector = hashedVector
        });
        return this;
    }

    /// <summary>
    /// Defines a geo-spatial index marker on the specified geometry property.
    /// SurrealDB uses a standard btree under the hood; spatial queries use
    /// bounding-box pre-filters and geo::DISTANCE post-filters.
    /// </summary>
    /// <param name="property">The property storing the geometry (GeometryPoint or GeometryPolygon).</param>
    public DocumentMapping<T> SpatialIndex<TProp>(
        Expression<Func<T, TProp>> property)
    {
        var member = ExtractMember(property);
        Indices.Add(new IndexDefinition
        {
            Columns = [member.Name],
            Name = $"geo_{Snake(typeof(T).Name)}_{Snake(member.Name)}",
            Type = IndexType.Geo
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
            Type = IndexType.Hnsw,
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

    /// <summary>
    /// No-op. Exists only for Marten portability — SurrealDB does not need duplicated fields.
    /// </summary>
    public DocumentMapping<T> Duplicate(
        Expression<Func<T, object?>> expression,
        string? pgType = null,
        object? dbType = null,
        Action<DocumentIndex>? configure = null,
        bool notNull = false)
    {
        return this;
    }

    /// <summary>
    /// No-op. Exists only for Marten portability — SurrealDB does not need duplicated fields.
    /// </summary>
    public DocumentMapping<T> Duplicate<TSub>(
        Expression<Func<TSub, object?>> expression,
        string? pgType = null,
        object? dbType = null,
        Action<DocumentIndex>? configure = null,
        bool notNull = false)
        where TSub : T
    {
        return this;
    }

    private static MemberInfo ExtractMember<TProp>(Expression<Func<T, TProp>> expression)
        => ExtractMemberFromBody(expression.Body);

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }

    private static MemberInfo ExtractMemberFromBody(Expression expr) => expr switch
    {
        MemberExpression me => me.Member,
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: MemberExpression me } => me.Member,
        _ => throw new ArgumentException($"Expression must refer to a property or field, not '{expr.NodeType}'.")
    };

    private static MemberInfo ExtractMemberFromArgument(Expression expr)
    {
        try
        {
            return ExtractMemberFromBody(expr);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                $"Each argument in a multi-column index anonymous type must be a property access (e.g. x => x.Prop). " +
                $"Constants and method calls are not allowed.");
        }
    }

    private static bool IsAnonymousType(Type type) =>
        type.Namespace == null
        && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
        && type.Name.Contains("<>");
}

/// <summary>
/// Defines a SurrealDB field with optional type, default, assertion, and permissions.
/// Emitted as DEFINE FIELD statements by the schema manager.
/// </summary>
public class FieldDefinition
{
    /// <summary>Field name in the SurrealDB table.</summary>
    public string FieldName { get; set; } = "";

    /// <summary>SurrealDB type (e.g., "string", "int", "datetime", "option&lt;string&gt;").</summary>
    public string? FieldType { get; set; }

    /// <summary>Default value expression (e.g., "0", "'default'", "time::now()").</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Assert expression for validation (e.g., "string::is::email($value)").</summary>
    public string? AssertExpression { get; set; }

    /// <summary>Permissions clause (e.g., "WHERE $auth.role = 'admin'").</summary>
    public string? Permissions { get; set; }
}
