using System.Text;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace Dali;

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
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, CancellationToken ct = default)
        where T : SurrealDb.Net.Models.IRecord
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table}", tableName);
        var schemaMode = typeof(T).IsSealed ? SchemaMode.Strict : SchemaMode.Flexible;
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(schemaMode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(typeof(T)))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a document table exists with the specified schema mode.
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, SchemaMode mode, CancellationToken ct = default)
        where T : SurrealDb.Net.Models.IRecord
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table} with mode {Mode}", tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(typeof(T)))
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
    private static IEnumerable<(string Name, string SurrealType)> GetFieldSchemas(Type type)
    {
        var meta = Metadata.MetadataRegistry.TryGet(type);
        if (meta?.Fields is { Count: > 0 } fields)
        {
            foreach (var f in fields)
                yield return (f.Name, f.SurrealType);
            yield break;
        }

        // Fallback: runtime reflection (legacy path for non-generated types)
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;
            yield return (prop.Name, GetSurrealType(prop.PropertyType));
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
        await session.RawQuery("DEFINE FIELD created_at ON TABLE mt_events TYPE datetime;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE INDEX mt_events_stream_version ON TABLE mt_events COLUMNS stream_id, version UNIQUE;", null, ct).ConfigureAwait(false);
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
    public async Task EnsureIndexAsync(ISurrealDbSession session, string tableName, IndexDefinition index, CancellationToken ct = default)
    {
        string surql = index.Type switch
        {
            IndexType.FullText => BuildFullTextIndex(tableName, index),
            IndexType.Hnsw => BuildHnswIndex(tableName, index),
            IndexType.Mtree => BuildMtreeIndex(tableName, index),
            IndexType.Diskann => BuildDiskannIndex(tableName, index),
            IndexType.Geo => BuildStandardIndex(tableName, index),
            _ => BuildStandardIndex(tableName, index)
        };

        _logger.LogDebug("Ensuring index {IndexName} on table {Table} (type: {Type})",
            index.Name, tableName, index.Type);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
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

    private static string BuildMtreeIndex(string tableName, IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns);
        var dim = index.VectorDimension ?? 1536;
        var dist = index.VectorDistance ?? Search.Distance.Cosine;
        return $"DEFINE INDEX {index.Name} ON TABLE {tableName} FIELDS {columns} MTREE DIMENSION {dim} DIST {dist};";
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
    /// Non-generic overload of <see cref="EnsureDocumentSchemaAsync{T}"/> for use without
    /// compile-time type knowledge (e.g. when iterating configured mappings).
    /// </summary>
    internal async Task EnsureDocumentSchemaAsync(Type entityType, ISurrealDbSession session, SchemaMode mode = SchemaMode.Strict, CancellationToken ct = default)
    {
        var tableName = MetadataDispatch.GetTableName(entityType);
        _logger.LogDebug("Ensuring document schema for type {Type} with table {Table} and mode {Mode}", entityType.Name, tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        foreach (var (name, surrealType) in GetFieldSchemas(entityType))
        {
            var fieldSurql = $"DEFINE FIELD {name} ON TABLE {tableName} TYPE {surrealType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
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

    public async Task DropTableAsync(ISurrealDbSession session, string tableName, CancellationToken ct = default)
    {
        _logger.LogDebug("Dropping table {Table}", tableName);
        await session.RawQuery($"REMOVE TABLE {tableName};", null, ct).ConfigureAwait(false);
    }

    private static string GetSurrealType(Type type)
    {
        // Check for geometry types first
        if (type == typeof(GeometryPoint) || type == typeof(GeometryPolygon))
            return "geometry";

        if (type == typeof(string) || type == typeof(Guid)) return "string";
        if (type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(byte)) return "int";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "float";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type == typeof(byte[])) return "bytes";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return "array";
        if (type.IsArray) return "array";
        return "object";
    }

    internal static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
