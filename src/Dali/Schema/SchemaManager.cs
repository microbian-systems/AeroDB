using SurrealDb.Net;

namespace Dali;

public class SchemaManager
{
    /// <summary>
    /// Ensures a document table exists with SCHEMAFULL mode and defines fields for all
    /// public readable/writable properties on T (except Id).
    /// </summary>
    public async Task EnsureDocumentSchemaAsync<T>(ISurrealDbSession session, CancellationToken ct = default)
        where T : SurrealDb.Net.Models.Record
    {
        var tableName = Snake(typeof(T).Name);
        await session.RawQuery($"DEFINE TABLE {tableName} SCHEMAFULL;", null, ct);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            // CBOR serialization stores C# property names as-is (PascalCase), so field definitions
            // must use PascalCase to match what the engine actually stores in SCHEMAFULL mode.
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
            await session.RawQuery(fieldSurql, null, ct);
        }
    }

    /// <summary>
    /// Ensures the mt_events table exists with the required schema for event sourcing.
    /// </summary>
    public async Task EnsureEventSchemaAsync(ISurrealDbSession session, CancellationToken ct = default)
    {
        await session.RawQuery("DEFINE TABLE mt_events SCHEMAFULL;", null, ct);
        await session.RawQuery("DEFINE FIELD stream_id ON TABLE mt_events TYPE string;", null, ct);
        await session.RawQuery("DEFINE FIELD version ON TABLE mt_events TYPE int;", null, ct);
        await session.RawQuery("DEFINE FIELD event_type ON TABLE mt_events TYPE string;", null, ct);
        await session.RawQuery("DEFINE FIELD data ON TABLE mt_events TYPE string;", null, ct);
        await session.RawQuery("DEFINE FIELD created_at ON TABLE mt_events TYPE datetime;", null, ct);
        await session.RawQuery("DEFINE INDEX mt_events_stream_version ON TABLE mt_events COLUMNS stream_id, version UNIQUE;", null, ct);
    }

    /// <summary>
    /// Convenience overload: creates a temporary session, ensures event schema, then disposes.
    /// </summary>
    internal async Task EnsureEventSchemaAsync(ISurrealDbClient client, string ns, string db, CancellationToken ct = default)
    {
        await using var session = await client.CreateSession(ct);
        await session.Use(ns, db, ct);
        await EnsureEventSchemaAsync(session, ct);
    }

    /// <summary>
    /// Legacy method: ensures a schema for the given type using explicit table name.
    /// Kept for backward compatibility.
    /// </summary>
    public async Task EnsureSchemaAsync<T>(ISurrealDbSession session, string tableName, CancellationToken ct = default)
    {
        await session.RawQuery($"DEFINE TABLE {tableName} SCHEMAFULL;", null, ct);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            var fieldSurql = $"DEFINE FIELD {prop.Name} ON TABLE {tableName} TYPE {fieldType};";
            await session.RawQuery(fieldSurql, null, ct);
        }
    }

    public async Task DropTableAsync(ISurrealDbSession session, string tableName, CancellationToken ct = default)
    {
        await session.RawQuery($"REMOVE TABLE {tableName};", null, ct);
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

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
