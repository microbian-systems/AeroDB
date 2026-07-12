using System.Reflection;
using System.Text;
using AeroDB.Sable.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace AeroDB.Sable;

public class SchemaManager
{
    private readonly ILogger<SchemaManager> _logger;

    public SchemaManager(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<SchemaManager>()
            ?? NullLogger<SchemaManager>.Instance;
    }

    /// <summary>
    /// Ensures a SurrealDB database exists by executing <c>DEFINE DATABASE IF NOT EXISTS</c>.
    /// The session must already be connected to the correct namespace.
    /// </summary>
    public async Task EnsureDatabaseAsync(ISurrealDbSession session, string databaseName, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring database {Database}", databaseName);
        await session.RawQuery($"DEFINE DATABASE IF NOT EXISTS `{databaseName}`;", null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures a document table exists with the configured schema mode and defines fields for all
    /// public readable/writable properties on T (except Id).
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, CancellationToken ct = default,
        EnumStorage enumStorage = EnumStorage.AsString)
        where T : class
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table}", tableName);
        var schemaMode = typeof(T).IsSealed ? SchemaMode.Strict : SchemaMode.Flexible;
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(schemaMode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(typeof(T), enumStorage: enumStorage))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a document table exists with the specified schema mode.
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, SchemaMode mode, CancellationToken ct = default,
        EnumStorage enumStorage = EnumStorage.AsString)
        where T : class
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table} with mode {Mode}", tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(typeof(T), enumStorage: enumStorage))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    private static string GetSchemaSurql(SchemaMode mode)
        => mode == SchemaMode.Flexible ? "SCHEMALESS" : "SCHEMAFULL";

    /// <summary>
    /// Returns the list of field schemas for the given type, using generated metadata
    /// when available, falling back to runtime reflection if not.
    /// </summary>
    private static IEnumerable<(string Name, string SurrealType)> GetFieldSchemas(
        Type type,
        SchemaOptions? schema = null,
        IReadOnlySet<string>? excludedClrNames = null,
        EnumStorage enumStorage = EnumStorage.AsString)
    {
        var nullability = new NullabilityInfoContext();
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => prop.Name != "Id"
                && prop.CanRead
                && prop.CanWrite
                && excludedClrNames?.Contains(prop.Name) != true)
            .ToDictionary(prop => prop.Name, StringComparer.Ordinal);

        var meta = Metadata.MetadataRegistry.TryGet(type);
        if (meta?.Fields is { Count: > 0 } fields)
        {
            foreach (var f in fields)
            {
                if (properties.TryGetValue(f.Name, out var prop))
                {
                    var st = MakeOptionalIfNullable(f.SurrealType, prop, nullability);
                    // Rewrite enum literals to integers when runtime config overrides source gen default
                    var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    if (enumStorage == EnumStorage.AsInteger && propType.IsEnum)
                    {
                        var literalType = BuildEnumLiteralType(propType, enumStorage);
                        var isOpt = st.StartsWith("option<", StringComparison.Ordinal);
                        st = isOpt ? $"option<{literalType}>" : literalType;
                    }
                    yield return (MetadataDispatch.GetFieldName(type, f.Name, schema), st);
                }
                else
                {
                    yield return (MetadataDispatch.GetFieldName(type, f.Name, schema), f.SurrealType);
                }
            }

            yield break;
        }

        // Fallback: runtime reflection (legacy path for non-generated types)
        foreach (var prop in properties.Values)
        {
            yield return (MetadataDispatch.GetFieldName(type, prop.Name, schema), GetSurrealType(prop.PropertyType, prop, nullability, enumStorage));
        }
    }

    /// <summary>
    /// Ensures the mt_events table exists with the required schema for event sourcing.
    /// </summary>
    public async Task EnsureEventSchemaAsync(ISurrealDbSession session, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring event schema (table mt_events)");
        await session.RawQuery("DEFINE TABLE mt_events SCHEMAFULL;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD stream_id ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD version ON TABLE mt_events TYPE int;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD sequence ON TABLE mt_events TYPE int;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD stream_key ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD event_type ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD data_json ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD data_binary ON TABLE mt_events TYPE option<bytes>;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD headers_json ON TABLE mt_events TYPE option<string>;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD created_at ON TABLE mt_events TYPE datetime;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE INDEX mt_events_stream_version ON TABLE mt_events COLUMNS stream_id, version UNIQUE;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE TABLE mt_archived_streams SCHEMAFULL;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD stream_id ON TABLE mt_archived_streams TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD archived_at ON TABLE mt_archived_streams TYPE datetime;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE INDEX mt_archived_streams_id ON TABLE mt_archived_streams COLUMNS stream_id UNIQUE;", null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience overload: creates a temporary session, ensures event schema, then disposes.
    /// </summary>
    internal async Task EnsureEventSchemaAsync(ISurrealDbClient client, string ns, string db, CancellationToken ct = default)
    {
        await using var session = await client.CreateSession(ct).ConfigureAwait(false);
        await session.Use(ns, db, ct).ConfigureAwait(false);
        await EnsureEventSchemaAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the mt_projection_progress table exists for tracking projection rebuild state.
    /// </summary>
    public async Task EnsureProjectionStateTableAsync(ISurrealDbSession session, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring projection progress state table (mt_projection_progress)");
        await session.RawQuery("DEFINE TABLE mt_projection_progress SCHEMALESS;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD projection_name ON TABLE mt_projection_progress TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD last_version ON TABLE mt_projection_progress TYPE int;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD last_updated ON TABLE mt_projection_progress TYPE datetime;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE INDEX idx_projection_progress_name ON TABLE mt_projection_progress COLUMNS projection_name UNIQUE;", null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the specified index exists on the given table.
    /// </summary>
    public async Task EnsureIndexAsync(
        ISurrealDbSession session,
        string tableName,
        IndexDefinition index,
        Type? entityType = null,
        SchemaOptions? schemaOptions = null,
        CancellationToken ct = default)
    {
        var resolvedIndex = ResolveIndexColumns(index, entityType, schemaOptions);
        string surql = index.Type switch
        {
            IndexType.FullText => BuildFullTextIndex(tableName, resolvedIndex),
            IndexType.Hnsw => BuildHnswIndex(tableName, resolvedIndex),
            IndexType.Diskann => BuildDiskannIndex(tableName, resolvedIndex),
            IndexType.Geo => BuildStandardIndex(tableName, resolvedIndex),
            _ => BuildStandardIndex(tableName, resolvedIndex)
        };

        _logger.LogDebug("Ensuring index {IndexName} on table {Table} (type: {Type})",
            index.Name, tableName, index.Type);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    private static IndexDefinition ResolveIndexColumns(
        IndexDefinition index,
        Type? entityType,
        SchemaOptions? schemaOptions)
    {
        if (entityType is null || schemaOptions is null)
            return index;

        var resolvedColumns = index.Columns
            .Select(column => MetadataDispatch.GetFieldName(entityType, column, schemaOptions))
            .ToArray();

        return new IndexDefinition
        {
            Name = index.Name,
            Columns = resolvedColumns,
            IsUnique = index.IsUnique,
            Type = index.Type,
            Analyzer = index.Analyzer,
            Bm25 = index.Bm25,
            VectorDimension = index.VectorDimension,
            VectorDistance = index.VectorDistance,
            VectorElementType = index.VectorElementType,
            DiskannDegree = index.DiskannDegree,
            DiskannLBuild = index.DiskannLBuild,
            DiskannAlpha = index.DiskannAlpha,
            HasHashedVector = index.HasHashedVector
        };
    }

    private static string BuildStandardIndex(string tableName, IndexDefinition index)
    {
        var unique = index.IsUnique ? " UNIQUE" : "";
        var columns = string.Join(", ", index.Columns);
        return $"DEFINE INDEX {index.Name} ON TABLE {tableName} COLUMNS {columns}{unique};";
    }

    private static string BuildFullTextIndex(string tableName, IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns);
        var analyzer = index.Analyzer ?? "simple";
        var sb = new StringBuilder();
        sb.Append($"DEFINE INDEX {index.Name} ON TABLE {tableName} FIELDS {columns} FULLTEXT ANALYZER {analyzer}");
        if (index.Bm25.HasValue)
            sb.Append($" BM25({index.Bm25.Value.K1}, {index.Bm25.Value.B})");
        sb.Append(';');
        return sb.ToString();
    }

    private static string BuildHnswIndex(string tableName, IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns);
        var dim = index.VectorDimension ?? 1536;
        var dist = index.VectorDistance ?? Search.Distance.Cosine;
        return $"DEFINE INDEX {index.Name} ON TABLE {tableName} FIELDS {columns} HNSW DIMENSION {dim} DIST {dist};";
    }

    private static string BuildDiskannIndex(string tableName, IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns);
        var dim = index.VectorDimension ?? 768;
        var dist = index.VectorDistance ?? Search.Distance.Cosine;
        var type = index.VectorElementType ?? "F32";

        var sb = new StringBuilder();
        sb.Append($"DEFINE INDEX {index.Name} ON TABLE {tableName} FIELDS {columns} DISKANN DIMENSION {dim} DIST {dist} TYPE {type}");

        if (index.DiskannDegree.HasValue)
            sb.Append($" DEGREE {index.DiskannDegree.Value}");
        if (index.DiskannLBuild.HasValue)
            sb.Append($" L_BUILD {index.DiskannLBuild.Value}");
        if (index.DiskannAlpha.HasValue && Math.Abs(index.DiskannAlpha.Value - 1.2) > 0.001)
            sb.Append($" ALPHA {index.DiskannAlpha.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (index.HasHashedVector)
            sb.Append(" HASHED_VECTOR");

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Ensures all configured analyzers exist in the database.
    /// </summary>
    public async Task EnsureAnalyzersAsync(ISurrealDbSession session, AnalyzerOptions options, CancellationToken ct = default)
    {
        foreach (var analyzer in options.Analyzers)
        {
            await EnsureAnalyzerAsync(session, analyzer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a single analyzer exists in the database.
    /// </summary>
    public async Task EnsureAnalyzerAsync(ISurrealDbSession session, AnalyzerDefinition analyzer, CancellationToken ct = default)
    {
        var tokenizers = string.Join(", ", analyzer.Tokenizers);
        var filters = analyzer.Filters.Length > 0
            ? " FILTERS " + string.Join(", ", analyzer.Filters.Select(f => $"{f}"))
            : "";
        var surql = $"DEFINE ANALYZER {analyzer.Name} TOKENIZERS {tokenizers}{filters};";
        _logger.LogDebug("Ensuring analyzer {Name}", analyzer.Name);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a DEFINE TABLE SurrealQL statement for an edge (relation) table.
    /// </summary>
    internal static string BuildDefineEdgeTable<TEdge>(EdgeMapping<TEdge> mapping) where TEdge : EdgeRecord
    {
        var sb = new StringBuilder();
        sb.Append("DEFINE TABLE `").Append(mapping.TableName).Append("` ");

        if (mapping.SchemaMode == SchemaMode.Strict)
            sb.Append("SCHEMAFULL ");
        else
            sb.Append("SCHEMALESS ");

        sb.Append("TYPE RELATION IN `").Append(mapping.FromTable)
          .Append("` OUT `").Append(mapping.ToTable).Append("`;");

        return sb.ToString();
    }

    /// <summary>
    /// Builds DEFINE INDEX SurrealQL statements for edge fields.
    /// </summary>
    internal static IEnumerable<string> BuildEdgeIndexStatements<TEdge>(EdgeMapping<TEdge> mapping) where TEdge : EdgeRecord
    {
        foreach (var field in mapping.Indexes)
        {
            yield return $"DEFINE INDEX idx_{mapping.TableName}_{field} ON TABLE `{mapping.TableName}` COLUMNS {field};";
        }
    }

    /// <summary>
    /// Ensures an edge (relation) table schema exists in the database.
    /// </summary>
    public async Task EnsureEdgeSchemaAsync<TEdge>(ISurrealDbSession session, EdgeMapping<TEdge> mapping, CancellationToken ct = default)
        where TEdge : EdgeRecord
    {
        var tableSql = BuildDefineEdgeTable(mapping);
        _logger.LogDebug("Ensuring edge schema for table {Table}", mapping.TableName);
        await session.RawQuery(tableSql, null, ct).ConfigureAwait(false);

        foreach (var indexSql in BuildEdgeIndexStatements(mapping))
        {
            _logger.LogDebug("Ensuring edge index on {Table}.{Field}", mapping.TableName, indexSql);
            await session.RawQuery(indexSql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Non-generic overload of <see cref="EnsureEdgeSchemaAsync{TEdge}"/> for use without
    /// compile-time type knowledge (e.g. when iterating configured edge mappings).
    /// </summary>
    internal async Task EnsureEdgeSchemaAsync(ISurrealDbSession session, object mapping, CancellationToken ct = default)
    {
        var mappingType = mapping.GetType();
        if (!mappingType.IsGenericType || mappingType.GetGenericTypeDefinition() != typeof(EdgeMapping<>))
        {
            _logger.LogWarning("Skipping unknown edge mapping type: {Type}", mappingType.Name);
            return;
        }

        var edgeType = mappingType.GetGenericArguments()[0];
        var tableName = (string)mappingType.GetProperty("TableName")!.GetValue(mapping)!;
        var fromTable = (string)mappingType.GetProperty("FromTable")!.GetValue(mapping)!;
        var toTable = (string)mappingType.GetProperty("ToTable")!.GetValue(mapping)!;
        var schemaMode = (SchemaMode)mappingType.GetProperty("SchemaMode")!.GetValue(mapping)!;
        var indexes = (IReadOnlyList<string>)mappingType.GetProperty("Indexes")!.GetValue(mapping)!;

        var schemaSurql = schemaMode == SchemaMode.Strict ? "SCHEMAFULL" : "SCHEMALESS";
        var sql = $"DEFINE TABLE `{tableName}` {schemaSurql} TYPE RELATION IN `{fromTable}` OUT `{toTable}`;";
        _logger.LogDebug("Ensuring edge schema for table {Table}", tableName);
        await session.RawQuery(sql, null, ct).ConfigureAwait(false);

        foreach (var field in indexes)
        {
            var indexSql = $"DEFINE INDEX idx_{tableName}_{field} ON TABLE `{tableName}` COLUMNS {field};";
            await session.RawQuery(indexSql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Non-generic overload of <c>EnsureDocumentSchemaAsync&lt;T&gt;</c> for use without
    /// compile-time type knowledge (e.g. when iterating configured mappings).
    /// </summary>
    internal async Task EnsureDocumentSchemaAsync(
        Type entityType,
        ISurrealDbSession session,
        SchemaMode mode = SchemaMode.Strict,
        IReadOnlyList<FieldDefinition>? fieldDefinitions = null,
        IReadOnlyList<RelationshipMapping>? relationshipMappings = null,
        SchemaOptions? schemaOptions = null,
        CancellationToken ct = default,
        EnumStorage enumStorage = EnumStorage.AsString)
    {
        var tableName = MetadataDispatch.GetTableName(entityType, schemaOptions);
        _logger.LogDebug("Ensuring document schema for type {Type} with table {Table} and mode {Mode}", entityType.Name, tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        var relationshipFieldNames = relationshipMappings?
            .Where(r => r.ClrMemberName is not null)
            .Select(r => r.ClrMemberName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (name, surrealType) in GetFieldSchemas(entityType, schemaOptions, relationshipFieldNames, enumStorage))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }

        // Ensure document metadata fields for types implementing IDocumentMetadata
        if (typeof(Metadata.IDocumentMetadata).IsAssignableFrom(entityType))
        {
            await session.RawQuery(
                $"DEFINE FIELD last_modified_by ON TABLE {tableName} TYPE option<string>;", null, ct).ConfigureAwait(false);
        }

        // Emit custom field definitions (assertions, defaults, permissions)
        if (fieldDefinitions is { Count: > 0 })
        {
            await EnsureFieldDefinitionsAsync(session, tableName, fieldDefinitions, ct).ConfigureAwait(false);
        }

        if (relationshipMappings is { Count: > 0 })
        {
            foreach (var relationship in relationshipMappings)
            {
                if (relationship.StorageKind == RelationshipStorageKind.ScalarForeignKey)
                    continue;

                var sql = BuildRelationshipFieldStatement(relationship, tableName);
                await session.RawQuery(sql, null, ct).ConfigureAwait(false);

                if (relationship.Unique)
                {
                    var indexSql = BuildRelationshipIndexStatement(relationship, tableName);
                    await session.RawQuery(indexSql, null, ct).ConfigureAwait(false);
                }
            }
        }
    }

    internal static string BuildRelationshipFieldStatement(RelationshipMapping relationship, string? tableName = null)
    {
        var sourceTable = tableName ?? relationship.SourceTableName;
        if (relationship.StorageKind == RelationshipStorageKind.ScalarForeignKey)
            throw new InvalidOperationException("Scalar foreign key relationships do not emit SurrealDB record-link field DDL.");

        var type = relationship.Cardinality == RelationshipCardinality.Many
            ? $"array<record<{relationship.TargetTableName}>>"
            : $"record<{relationship.TargetTableName}>";

        if (relationship.Nullable)
            type = $"option<{type}>";

        var sb = new StringBuilder();
        sb.Append("DEFINE FIELD ").Append(relationship.StorageFieldName)
            .Append(" ON TABLE ").Append(sourceTable)
            .Append(" TYPE ").Append(type);

        if (relationship.Reference)
        {
            sb.Append(" REFERENCE");
            if (relationship.OnDelete is not null)
            {
                sb.Append(" ON DELETE ");
                sb.Append(relationship.OnDelete.Value switch
                {
                    RelationshipOnDeleteAction.Ignore => "IGNORE",
                    RelationshipOnDeleteAction.Unset => "UNSET",
                    RelationshipOnDeleteAction.Cascade => "CASCADE",
                    RelationshipOnDeleteAction.Then => "THEN " + relationship.OnDeleteThenSurql,
                    _ => "IGNORE"
                });
            }
        }

        sb.Append(';');
        return sb.ToString();
    }

    internal static string BuildRelationshipIndexStatement(RelationshipMapping relationship, string? tableName = null)
    {
        var sourceTable = tableName ?? relationship.SourceTableName;
        var indexName = $"uidx_{sourceTable}_{relationship.StorageFieldName}";
        return $"DEFINE INDEX {indexName} ON TABLE {sourceTable} COLUMNS {relationship.StorageFieldName} UNIQUE;";
    }

    /// <summary>
    /// Ensures a SurrealDB pre-computed/aggregate view exists by executing
    /// <c>DEFINE TABLE IF NOT EXISTS view_name AS SELECT ...</c>.
    /// </summary>
    /// <typeparam name="T">The entity type representing the view's result shape.</typeparam>
    /// <param name="session">The SurrealDB session to execute against.</param>
    /// <param name="view">The view definition to ensure.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureViewAsync<T>(
        ISurrealDbSession session,
        ViewDefinition<T> view,
        CancellationToken ct = default) where T : class
    {
        var viewName = view.ViewName;
        _logger.LogInformation("Ensuring view {View}", viewName);

        if (view.IsDrop)
        {
            var dropSurql = $"DEFINE TABLE IF NOT EXISTS {viewName} DROP;";
            _logger.LogDebug("View (DROP) SurrealQL: {Surql}", dropSurql);
            await session.RawQuery(dropSurql, null, ct).ConfigureAwait(false);
            return;
        }

        var selectSurql = view.BuildSelectSurql();
        var surql = $"DEFINE TABLE IF NOT EXISTS {viewName} AS {selectSurql};";
        _logger.LogDebug("View SurrealQL: {Surql}", surql);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Legacy method: ensures a schema for the given type using explicit table name.
    /// Kept for backward compatibility.
    /// </summary>
    public async Task EnsureSchemaAsync<T>(ISurrealDbSession session, string tableName, SchemaMode mode = SchemaMode.Strict, CancellationToken ct = default)
    {
        _logger.LogDebug("Ensuring schema for type {Type} with table {Table} and mode {Mode}", typeof(T).Name, tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(typeof(T)))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues DEFINE ACCESS for configured access definitions (SurrealDB v3+).
    /// </summary>
    public async Task EnsureAccessesAsync(ISurrealDbSession session, List<AccessDefinition> accesses, CancellationToken ct = default)
    {
        foreach (var access in accesses)
        {
            var sb = new StringBuilder();
            sb.Append("DEFINE ACCESS ").Append(access.Name);
            sb.Append(" ON DATABASE TYPE ").Append(access.Type);
            if (access.SignupQuery is not null)
                sb.Append(" SIGNUP ( ").Append(access.SignupQuery).Append(" )");
            if (access.SigninQuery is not null)
                sb.Append(" SIGNIN ( ").Append(access.SigninQuery).Append(" )");
            if (access.Duration is not null)
                sb.Append(" DURATION FOR TOKEN ").Append(access.Duration);
            sb.Append(';');
            _logger.LogDebug("Ensuring access {Name}", access.Name);
            await session.RawQuery(sb.ToString(), null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues DEFINE TOKEN for configured JWT/HMAC token definitions.
    /// </summary>
    public async Task EnsureTokensAsync(ISurrealDbSession session, List<TokenDefinition> tokens, CancellationToken ct = default)
    {
        foreach (var token in tokens)
        {
            var surql = $"DEFINE TOKEN {token.Name} ON DATABASE TYPE {token.Type} VALUE \"{token.Value}\";";
            _logger.LogDebug("Ensuring token {Name} (type: {Type})", token.Name, token.Type);
            await session.RawQuery(surql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues DEFINE SCOPE for configured scope definitions (user signup/signin).
    /// </summary>
    public async Task EnsureScopesAsync(ISurrealDbSession session, List<ScopeDefinition> scopes, CancellationToken ct = default)
    {
        foreach (var scope in scopes)
        {
            var sb = new StringBuilder();
            sb.Append("DEFINE SCOPE ").Append(scope.Name);
            sb.Append(" SESSION ").Append(scope.SessionDuration);
            if (scope.SignupQuery is not null)
                sb.Append(" SIGNUP ( ").Append(scope.SignupQuery).Append(" )");
            if (scope.SigninQuery is not null)
                sb.Append(" SIGNIN ( ").Append(scope.SigninQuery).Append(" )");
            sb.Append(';');
            _logger.LogDebug("Ensuring scope {Name}", scope.Name);
            await session.RawQuery(sb.ToString(), null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues DEFINE FIELD for custom field definitions (assertions, defaults, permissions).
    /// Called after the base field definitions from <see cref="EnsureDocumentSchemaAsync"/>.
    /// </summary>
    public async Task EnsureFieldDefinitionsAsync(
        ISurrealDbSession session, string tableName, IReadOnlyList<FieldDefinition> fields, CancellationToken ct = default)
    {
        foreach (var field in fields)
        {
            if (field.Remove)
            {
                var removeSurql = $"REMOVE FIELD {field.FieldName} ON TABLE {tableName};";
                _logger.LogDebug("Removing field definition {Field} on table {Table}", field.FieldName, tableName);
                await session.RawQuery(removeSurql, null, ct).ConfigureAwait(false);
                continue;
            }

            var sb = new StringBuilder();
            sb.Append("DEFINE FIELD ").Append(field.FieldName);
            sb.Append(" ON TABLE ").Append(tableName);
            if (field.FieldType is not null)
                sb.Append(" TYPE ").Append(field.FieldType);
            if (field.DefaultValue is not null)
                sb.Append(" DEFAULT ").Append(field.DefaultValue);
            if (field.AssertExpression is not null)
                sb.Append(" ASSERT ").Append(field.AssertExpression);
            if (field.Permissions is not null)
                sb.Append(" PERMISSIONS FOR ").Append(field.Permissions);
            sb.Append(';');
            _logger.LogDebug("Ensuring field definition {Field} on table {Table}", field.FieldName, tableName);
            await session.RawQuery(sb.ToString(), null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Alters a table's CHANGEFEED clause. Used by ChangeTrackingSchemaManager during initialization.
    /// </summary>
    public async Task AlterTableChangeFeedAsync(ISurrealDbSession session, string tableName, string changeFeedClause, CancellationToken ct = default)
    {
        _logger.LogDebug("Altering CHANGEFEED for table {Table}: {Clause}", tableName, changeFeedClause);
        await session.RawQuery($"ALTER TABLE {tableName} {changeFeedClause};", null, ct).ConfigureAwait(false);
    }

    public async Task DropTableAsync(ISurrealDbSession session, string tableName, CancellationToken ct = default)
    {
        _logger.LogDebug("Dropping table {Table}", tableName);
        await session.RawQuery($"REMOVE TABLE {tableName};", null, ct).ConfigureAwait(false);
    }

    private static string GetSurrealType(Type type, PropertyInfo? property = null, NullabilityInfoContext? nullability = null,
        EnumStorage enumStorage = EnumStorage.AsString)
    {
        var underlyingNullableType = Nullable.GetUnderlyingType(type);
        var isNullable = underlyingNullableType is not null
            || (property is not null && IsNullableReferenceProperty(property, nullability));
        var effectiveType = underlyingNullableType ?? type;

        // Check for enums first — generate literal type constraints (e.g. "Active" | "Inactive")
        if (effectiveType.IsEnum)
        {
            var literalType = BuildEnumLiteralType(effectiveType, enumStorage);
            return isNullable ? $"option<{literalType}>" : literalType;
        }

        // Check for geometry types
        var surrealType =
            effectiveType == typeof(GeometryPoint) || effectiveType == typeof(GeometryPolygon) ? "geometry" :
            effectiveType == typeof(string) || effectiveType == typeof(Guid) ? "string" :
            effectiveType == typeof(long) || effectiveType == typeof(int) || effectiveType == typeof(short) || effectiveType == typeof(byte) ? "int" :
            effectiveType == typeof(float) || effectiveType == typeof(double) || effectiveType == typeof(decimal) ? "float" :
            effectiveType == typeof(bool) ? "bool" :
            effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset) ? "datetime" :
            effectiveType == typeof(byte[]) ? "bytes" :
            effectiveType.IsGenericType && effectiveType.GetGenericTypeDefinition() is var gtd && (
                gtd == typeof(List<>) ||
                gtd == typeof(IList<>) ||
                gtd == typeof(ICollection<>) ||
                gtd == typeof(IReadOnlyList<>) ||
                gtd == typeof(IReadOnlyCollection<>) ||
                gtd == typeof(ISet<>)
            ) ? "array" :
            effectiveType.IsArray ? "array" :
            "object";

        return isNullable ? $"option<{surrealType}>" : surrealType;
    }

    private static string BuildEnumLiteralType(Type enumType, EnumStorage storage)
    {
        var names = Enum.GetNames(enumType);
        var literals = storage == EnumStorage.AsInteger
            ? names.Select(n => Convert.ToInt64(Enum.Parse(enumType, n)).ToString())
            : names.Select(n => $"\"{n}\"");
        return string.Join(" | ", literals);
    }

    private static string MakeOptionalIfNullable(string surrealType, PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (surrealType.StartsWith("option<", StringComparison.Ordinal))
            return surrealType;

        var isNullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
            || IsNullableReferenceProperty(property, nullability);
        return isNullable ? $"option<{surrealType}>" : surrealType;
    }

    private static bool IsNullableReferenceProperty(PropertyInfo property, NullabilityInfoContext? nullability)
    {
        if (property.PropertyType.IsValueType)
            return false;

        var context = nullability ?? new NullabilityInfoContext();
        return context.Create(property).WriteState == NullabilityState.Nullable;
    }

    internal static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
