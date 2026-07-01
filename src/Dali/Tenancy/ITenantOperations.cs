namespace Dali;

/// <summary>
/// Cross-tenant document operations within a session.
/// Provides access to per-tenant query sessions for multi-tenancy scenarios.
/// </summary>
/// <remarks>
/// Marten parity: <c>ITenantOperations</c> / <c>ITenantQueryOperations</c> provide
/// <c>ForTenant(tenantId) => IQuerySession</c> to scope operations to a specific tenant.
/// <br/>
/// <b>Existing Dali API:</b> Dali already achieves the same capabilities through:
/// <list type="bullet">
///   <item><description><see cref="IDocumentStore.WithTenant(string)"/> — sets tenant for the next session creation.</description></item>
///   <item><description><see cref="IQuerySession.SetTenant(string)"/> — scopes a session to a tenant.</description></item>
///   <item><description><see cref="IDocumentSession.ForTenant(string)"/> — returns a session scoped to a tenant.</description></item>
/// </list>
/// This interface provides a unified Marten-compatible surface over those APIs.
/// </remarks>
public interface ITenantOperations
{
    /// <summary>
    /// Returns a query session scoped to the specified tenant.
    /// All subsequent queries and operations are isolated to that tenant's data.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <returns>A query session scoped to the given tenant.</returns>
    IQuerySession ForTenant(string tenantId);
}

/// <summary>
/// Tenant-scoped query operations. Provides document queries filtered by tenant.
/// </summary>
/// <remarks>
/// In Dali, <see cref="ITenantQueryOperations"/> is implemented by the store
/// and delegates to the existing <see cref="IDocumentStore.WithTenant(string)"/>
/// and session-level <see cref="IQuerySession.SetTenant(string)"/> mechanisms.
/// </remarks>
public interface ITenantQueryOperations
{
    /// <summary>
    /// Creates a tenant-scoped query session for read-only access.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IQuerySession> QueryAsync(string tenantId, CancellationToken ct = default);
}
