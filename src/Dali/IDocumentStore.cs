using SurrealDb.Net;

namespace Dali;

public interface IDocumentStore : IAsyncDisposable
{
    Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default);
    Task<IDocumentSession> LightweightSessionAsync(CancellationToken ct = default);
    Task<IDocumentSession> DocumentSessionAsync(CancellationToken ct = default);
    StoreOptions Options { get; }
    ISurrealDbClient Client { get; }
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the tenant ID for the next session created from this store (DatabasePerTenant mode).
    /// The tenant ID is consumed on the next call to <c>QuerySessionAsync</c>,
    /// <c>LightweightSessionAsync</c>, or <c>DocumentSessionAsync</c>.
    /// </summary>
    IDocumentStore WithTenant(string tenantId);
}

public interface IQuerySession : IAsyncDisposable
{
    Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class;
    ISurrealDbQueryable<T> Query<T>() where T : class;

    /// <summary>
    /// Executes a raw SurrealQL query and returns the deserialized results.
    /// </summary>
    Task<List<T>> RawQueryAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Executes a raw SurrealQL statement that does not return rows (e.g., CREATE, UPDATE, DELETE, DEFINE).
    /// Returns the number of affected records or -1 if unknown.
    /// </summary>
    Task<int> ExecuteSqlAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// The tenant ID for this session (null if no tenancy is configured).
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Sets the tenant context for this session, scoping all subsequent operations to the given tenant.
    /// </summary>
    void SetTenant(string tenantId);

    /// <summary>
    /// Clears the tenant context from this session.
    /// </summary>
    void ClearTenant();
}

public interface IDocumentSession : IQuerySession
{
    void Store<T>(T entity) where T : class;
    void Delete<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    void ClearChanges();
    IEvents Events { get; }
}
