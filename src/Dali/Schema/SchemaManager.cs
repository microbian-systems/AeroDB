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
    /// Ensures a document table exists with the configured schema mode and defines fields for all
    /// public readable/writable properties on T (except Id).
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, CancellationToken ct = default)
        where T : SurrealDb.Net.Models.Record
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table}", tableName);
        var schemaMode = typeof(T).IsSealed ? SchemaMode.Strict : SchemaMode.Flexible;
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(schemaMode)};", null, ct).ConfigureAwait(false);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            // CBOR serialization stores C# property names as-is (PascalCase), so field definitions
            // must use PascalCase to match what the engine actually stores in strict mode.
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ensures a document table exists with the specified schema mode.
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, SchemaMode mode, CancellationToken ct = default)
        where T : SurrealDb.Net.Models.Record
    {
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogDebug("Ensuring document schema for table {Table} with mode {Mode}", tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    private static string GetSchemaSurql(SchemaMode mode)
        => mode == SchemaMode.Flexible ? "SCHEMALESS" : "SCHEMAFULL";

    /// <summary>
    /// Ensures the mt_events table exists with the required schema for event sourcing.
    /// </summary>
    public async Task EnsureEventSchemaAsync(ISurrealDbSession session, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring event schema (table mt_events)");
        await session.RawQuery("DEFINE TABLE mt_events SCHEMAFULL;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD stream_id ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD version ON TABLE mt_events TYPE int;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD event_type ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
        await session.RawQuery("DEFINE FIELD data ON TABLE mt_events TYPE string;", null, ct).ConfigureAwait(false);
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
    /// Ensures the specified index exists on the given table.
    /// </summary>
    public async Task EnsureIndexAsync(ISurrealDbSession session, string tableName, IndexDefinition index, CancellationToken ct = default)
    {
        string surql = index.Type switch
        {
            IndexType.FullText => BuildFullTextIndex(tableName, index),
            IndexType.Vector => BuildVectorIndex(tableName, index),
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

    private static string BuildVectorIndex(string tableName, IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns);
        var dim = index.VectorDimension ?? 1536;
        var dist = index.VectorDistance ?? Search.Distance.Cosine;
        return $"DEFINE INDEX {index.Name} ON TABLE {tableName} FIELDS {columns} HNSW DIMENSION {dim} DIST {dist};";
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
            ? " " + string.Join(", ", analyzer.Filters.Select(f => $"FILTERS {f}"))
            : "";
        var surql = $"DEFINE ANALYZER {analyzer.Name} TOKENIZERS {tokenizers}{filters};";
        _logger.LogDebug("Ensuring analyzer {Name}", analyzer.Name);
        await session.RawQuery(surql, null, ct).ConfigureAwait(false);
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

        foreach (var prop in entityType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
            await session.RawQuery(fieldSurql, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Legacy method: ensures a schema for the given type using explicit table name.
    /// Kept for backward compatibility.
    /// </summary>
    public async Task EnsureSchemaAsync<T>(ISurrealDbSession session, string tableName, SchemaMode mode = SchemaMode.Strict, CancellationToken ct = default)
    {
        _logger.LogDebug("Ensuring schema for type {Type} with table {Table} and mode {Mode}", typeof(T).Name, tableName, mode);
        await session.RawQuery($"DEFINE TABLE {tableName} {GetSchemaSurql(mode)};", null, ct).ConfigureAwait(false);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
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
