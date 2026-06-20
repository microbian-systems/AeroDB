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
}

public interface IQuerySession : IAsyncDisposable
{
    Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class;
    ISurrealDbQueryable<T> Query<T>() where T : class;
}

public interface IDocumentSession : IQuerySession
{
    void Store<T>(T entity) where T : class;
    void Delete<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    IEvents Events { get; }
}
