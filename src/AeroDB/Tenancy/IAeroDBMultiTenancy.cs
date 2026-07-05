namespace AeroDB;

/// <summary>
/// Fluent interface for configuring a single-server multi-tenancy strategy
/// where each tenant maps to a separate database on the same SurrealDB server.
/// </summary>
public interface ISingleServerMultiTenancy
{
    ISingleServerMultiTenancy WithTenant(string tenantId, string database);
    ISingleServerMultiTenancy WithTenant(string tenantId, string database, string namespace_);
}

/// <summary>
/// Fluent interface for configuring static multi-tenancy where each tenant
/// maps to an explicit connection string or endpoint/ns/db triple.
/// </summary>
public interface IStaticMultiTenancy
{
    IStaticMultiTenancy WithTenant(string tenantId, string connectionString);
    IStaticMultiTenancy WithTenant(string tenantId, string endpoint, string ns, string db);
}
