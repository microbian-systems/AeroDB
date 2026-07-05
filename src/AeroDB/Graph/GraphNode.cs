using System.Text.Json.Serialization;

namespace AeroDB;

/// <summary>A graph node result from wildcard or path traversal.</summary>
public class GraphNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("table")]
    public string Table { get; set; } = "";
}

/// <summary>A graph edge result from wildcard or path traversal.</summary>
public class GraphEdge
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("in")]
    public string In { get; set; } = "";

    [JsonPropertyName("out")]
    public string Out { get; set; } = "";
}

/// <summary>A full graph path with ordered nodes and edges.</summary>
public class GraphPath
{
    [JsonPropertyName("nodes")]
    public List<GraphNode> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<GraphEdge> Edges { get; set; } = new();
}
