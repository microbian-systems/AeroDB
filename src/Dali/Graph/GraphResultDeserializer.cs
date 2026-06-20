using System.Text.Json;
using SurrealDb.Net.Models.Response;

namespace Dali;

internal static class GraphResultDeserializer
{
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Deserialize graph query results to a list of typed nodes.</summary>
    public static List<T> DeserializeNodes<T>(SurrealDbResponse response)
    {
        var raw = response.GetValue<List<object>>(0);
        if (raw == null) return new List<T>();
        var json = JsonSerializer.Serialize(raw);
        return JsonSerializer.Deserialize<List<T>>(json, SnakeCaseOptions) ?? new List<T>();
    }

    /// <summary>Deserialize graph query results to a list of paths.</summary>
    public static List<GraphPath> DeserializePaths(SurrealDbResponse response)
    {
        // Path results come as complex nested objects from SurrealDB.
        // Each path contains nodes + edges arrays.
        var raw = response.GetValue<List<object>>(0);
        if (raw == null) return new List<GraphPath>();

        var paths = new List<GraphPath>();
        foreach (var item in raw)
        {
            var json = JsonSerializer.Serialize(item);
            var path = JsonSerializer.Deserialize<GraphPath>(json, SnakeCaseOptions);
            if (path != null) paths.Add(path);
        }
        return paths;
    }
}
