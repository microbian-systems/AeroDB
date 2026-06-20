// ============================================================
// SurrealDB EF Core LINQ Provider
// Core Models & Abstractions
// ============================================================

using System.Text.Json.Serialization;

namespace SurrealEFCore.Core;

// ── Record Identity ──────────────────────────────────────────

/// <summary>
/// Represents a SurrealDB record ID: "table:id"
/// e.g. "person:alice" or "order:01HJKM..."
/// </summary>
public readonly record struct RecordId(string Table, string Id)
{
    public override string ToString() => $"{Table}:{Id}";

    public static RecordId Parse(string raw)
    {
        var idx = raw.IndexOf(':');
        if (idx < 0) throw new FormatException($"Invalid RecordId: {raw}");
        return new(raw[..idx], raw[(idx + 1)..]);
    }

    public static implicit operator string(RecordId r) => r.ToString();
    public static implicit operator RecordId(string s) => Parse(s);
}

// ── Base Entities ─────────────────────────────────────────────

/// <summary>Base for all SurrealDB document entities.</summary>
public abstract class SurrealEntity
{
    [JsonPropertyName("id")]
    public RecordId Id { get; set; }
}

/// <summary>Typed document entity with a strongly-typed ID.</summary>
public abstract class SurrealDocument<T> : SurrealEntity
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Graph Edge ────────────────────────────────────────────────

/// <summary>
/// Represents a SurrealDB graph relation (edge).
/// RELATE person:alice -> knows -> person:bob
/// </summary>
public abstract class SurrealEdge<TIn, TOut> : SurrealEntity
    where TIn  : SurrealEntity
    where TOut : SurrealEntity
{
    [JsonPropertyName("in")]
    public RecordId In  { get; set; }

    [JsonPropertyName("out")]
    public RecordId Out { get; set; }

    [JsonIgnore]
    public TIn?  InEntity  { get; set; }

    [JsonIgnore]
    public TOut? OutEntity { get; set; }
}

// ── Domain Sample Models ──────────────────────────────────────

public class Person : SurrealDocument<Person>
{
    [JsonPropertyName("name")]  public string Name  { get; set; } = "";
    [JsonPropertyName("age")]   public int    Age   { get; set; }
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("tags")]  public List<string> Tags { get; set; } = new();
}

public class Product : SurrealDocument<Product>
{
    [JsonPropertyName("name")]     public string  Name     { get; set; } = "";
    [JsonPropertyName("price")]    public decimal Price    { get; set; }
    [JsonPropertyName("category")] public string  Category { get; set; } = "";
}

/// <summary>Graph edge: person KNOWS person.</summary>
public class Knows : SurrealEdge<Person, Person>
{
    [JsonPropertyName("since")] public DateTimeOffset Since { get; set; }
    [JsonPropertyName("strength")] public double Strength  { get; set; }
}

/// <summary>Graph edge: person PURCHASED product.</summary>
public class Purchased : SurrealEdge<Person, Product>
{
    [JsonPropertyName("quantity")] public int      Quantity  { get; set; }
    [JsonPropertyName("total")]    public decimal  Total     { get; set; }
    [JsonPropertyName("at")]       public DateTimeOffset At  { get; set; }
}
