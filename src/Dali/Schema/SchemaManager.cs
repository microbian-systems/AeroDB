using SurrealDb.Net;

namespace Dali;

public class SchemaManager
{
    public async Task EnsureSchemaAsync<T>(ISurrealDbSession session, string tableName, CancellationToken ct = default)
    {
        await session.RawQuery($"DEFINE TABLE {tableName} SCHEMAFULL;", null, ct);

        var type = typeof(T);
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == "Id") continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var fieldType = GetSurrealType(prop.PropertyType);
            var fieldSurql = $"DEFINE FIELD {Snake(prop.Name)} ON TABLE {tableName} TYPE {fieldType};";
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
