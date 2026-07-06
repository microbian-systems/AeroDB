using SurrealDb.Net.Models;

namespace AeroDB;

internal sealed class IncludeSpec
{
    /// <summary>C# property name (e.g. "Customer")</summary>
    public string PropertyName { get; set; } = "";
    /// <summary>SurrealDB target table name (e.g. "customer")</summary>
    public string TargetTable { get; set; } = "";
    /// <summary>Foreign key field on parent (e.g. "customer") — SurrealQL field name</summary>
    public string ForeignKeyField { get; set; } = "";
    /// <summary>C# property name for reflection (e.g. "Customer") — may differ when naming policy transforms field names</summary>
    public string ForeignKeyClrName { get; set; } = "";
    /// <summary>The included document type.</summary>
    public Type IncludeType { get; set; } = null!;
    /// <summary>True for forward include (single record&lt;T&gt;), false for reverse (collection)</summary>
    public bool IsSingle { get; set; }
    /// <summary>True for forward include (FK on parent), false for reverse (FK on child). Default: true.</summary>
    public bool IsForward { get; set; } = true;
    /// <summary>
    /// The SurrealDB field name for the parent's ID in the reverse-include subquery.
    /// "id" for Record types (RecordId), "Id" for Entity types (typed Id property).
    /// </summary>
    public string ParentIdField { get; set; } = "id";
}
