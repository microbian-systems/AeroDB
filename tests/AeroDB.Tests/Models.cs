using AeroDB.Sable;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

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
/// Model implementing <see cref="IDocumentMetadata"/> for document metadata audit tests.
/// </summary>
public class TimestampedDoc : Record, IDocumentMetadata
{
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
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

/// <summary>
/// Model for Fetch with typed record&lt;T&gt; properties.
/// Customer is a typed Record property (not RecordId?) so FETCH can expand
/// the reference into the full object.
/// </summary>
public class OrderWithCustomer : Record
{
    public string Product { get; set; } = "";
    public Customer? Customer { get; set; }
}

/// <summary>
/// Customer model referenced by OrderWithCustomer.Customer.
/// A simple Record that SurrealDB can expand inline via FETCH.
/// </summary>
public class Customer : Record
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>
/// DTO for testing dot-walk projections with MemberInitExpression.
/// Used by Select_DotWalk_ChainedMemberExpression tests.
/// </summary>
public class OrderCustomerDto
{
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string Product { get; set; } = "";
}

/// <summary>
/// DTO for testing full record property Select projections.
/// Used by Select_Dto_FullRecordProperty_AutoFetchesAndExpands tests.
/// </summary>
public class FullOrderDto
{
    public Customer? Customer { get; set; }
    public string Product { get; set; } = "";
}

/// <summary>
/// Model with two typed Record properties for multi-FETCH testing.
/// </summary>
public class MultiRefOrder : Record
{
    public Customer? Customer { get; set; }
    public ProjectRef? Project { get; set; }
    public string Product { get; set; } = "";
}

/// <summary>
/// DTO with two Record properties for multi-FETCH testing.
/// </summary>
public class MultiRefOrderDto
{
    public Customer? Customer { get; set; }
    public ProjectRef? Project { get; set; }
    public string Product { get; set; } = "";
}

/// <summary>
/// DTO with a renamed Record property — different name from the entity property.
/// Used to verify that renamed properties use alias and don't auto-FETCH.
/// </summary>
public class RenamedOrderDto
{
    public Customer? Client { get; set; }  // maps from OrderWithCustomer.Customer
    public string Item { get; set; } = ""; // maps from OrderWithCustomer.Product
}

// ──────────────────────────────────────────────
// Geo-spatial and time-series test models
// ──────────────────────────────────────────────

public class Store : Record
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public GeometryPoint Location { get; set; } = new();
    public GeometryPolygon? DeliveryZone { get; set; }
}

public class SensorReading : Record
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
    public string SensorId { get; set; } = "";
}

// ──────────────────────────────────────────────
// Reverse Include test models
// ──────────────────────────────────────────────

/// <summary>
/// Model for reverse include: parent with a collection of child records.
/// </summary>
public class OrderWithItems : Record
{
    public string Name { get; set; } = "";
    /// <summary>Loaded via IncludeReverse — not stored directly in SurrealDB</summary>
    public List<OrderLineItem> Items { get; set; } = new();
}

/// <summary>
/// Child record for reverse include. Has a ForeignKey "Order" pointing back to parent.
/// </summary>
public class OrderLineItem : Record
{
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public RecordId? Order { get; set; }  // FK back to OrderWithItems
}

/// <summary>
/// Full Client Order model for graph integration tests.
/// Has both a forward Record link (Customer) and a reverse collection (Items).
/// </summary>
public class ClientOrder : Record
{
    public Customer? Customer { get; set; }
    public string Description { get; set; } = "";
    public decimal Total { get; set; }
    public List<OrderLineItem> Items { get; set; } = new();
}

/// <summary>
/// DTO for testing full graph projection tests.
/// </summary>
public class ClientOrderDto
{
    public string CustomerName { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Total { get; set; }
}

// ──────────────────────────────────────────────
// Entity<TId> test models (non-Record, typed Ids)
// ──────────────────────────────────────────────

public class EntityProduct : EntitySnowlake
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public class EntityCustomer : EntityString
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public class EntityOrder : EntityInt
{
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}

public class EntitySession : EntityGuid
{
    public string Token { get; set; } = "";
    public string UserName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
