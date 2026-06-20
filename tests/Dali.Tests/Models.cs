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

// ──────────────────────────────────────────────
// Graph test edges
// ──────────────────────────────────────────────

public class Knows : EdgeRecord
{
    public int Since { get; set; }
    public string? Kind { get; set; }
}

public class WorksIn : EdgeRecord
{
    public string Role { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
}

public class ChildOf : EdgeRecord
{
}

public class Created : EdgeRecord
{
    public DateTimeOffset CreatedAt { get; set; }
}

// ──────────────────────────────────────────────
// Fetch / Include test models
// ──────────────────────────────────────────────

/// <summary>
/// Model for Fetch (FETCH clause) tests.
/// RelatedId is a RecordId? field that SurrealDB can expand inline.
/// </summary>
public class FetchableRecord : Record
{
    public RecordId? RelatedId { get; set; }
    public string Data { get; set; } = "";
}

/// <summary>
/// Model for Include tests: the "source" entity with foreign keys.
/// Foreign keys are RecordId? to match the target's Id type for
/// the Include mechanism's dictionary lookup.
/// </summary>
public class Issue : Record
{
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public RecordId? AssigneeId { get; set; }
    public RecordId? ProjectId { get; set; }
}

/// <summary>
/// Model for Include tests: the "target" entity.
/// Inherits RecordId? Id from Record.
/// </summary>
public class User : Record
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>
/// Model for Include tests: a second target entity type.
/// </summary>
public class ProjectRef : Record
{
    public string Name { get; set; } = "";
}
