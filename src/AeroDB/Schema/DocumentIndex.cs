namespace AeroDB;

/// <summary>
/// No-op stub for Marten portability. In Marten, this configures a database index
/// on a duplicated field. SurrealDB does not support duplicated fields, so all
/// members are no-ops.
/// </summary>
public class DocumentIndex
{
    public bool IsUnique { get; set; }
    public string? Name { get; set; }
    public TenancyScope TenancyScope { get; set; }
    public SortOrder SortOrder { get; set; }
}

/// <summary>
/// No-op stub for Marten portability. SurrealDB does not use tenancy-scoped indexes.
/// </summary>
public enum TenancyScope
{
    Global,
    PerTenant
}

/// <summary>
/// No-op stub for Marten portability. SurrealDB does not use index sort orders.
/// </summary>
public enum SortOrder
{
    Asc,
    Desc
}
