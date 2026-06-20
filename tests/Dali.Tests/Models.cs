using SurrealDb.Net.Models;

namespace Dali.Tests;

public class Person : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Email { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public class Product : Record
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public string Category { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Order : Record
{
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Tenant-aware model used for tenancy tests.
/// Has a TenantId property that the conjoined tenancy layer automatically populates and filters on.
/// </summary>
public class TenantPerson : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string TenantId { get; set; } = "";
}

/// <summary>
/// Model implementing <see cref="IVersioned"/> for optimistic concurrency tests.
/// </summary>
public class VersionedPerson : Person, IVersioned
{
    public long Version { get; set; }
}

/// <summary>
/// Model with a <see cref="VersionAttribute"/>-decorated property for optimistic concurrency tests.
/// </summary>
public class AttributedPerson : Person
{
    [Version]
    public long DocumentVersion { get; set; }
}

/// <summary>
/// Model for search index tests (full-text + vector).
/// </summary>
public class SearchablePage : Record
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public float[] Embedding { get; set; } = [];
}
