using AeroDB;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB-backed saga storage for Wolverine using SurrealDB as the document store.
/// </summary>
public sealed class AeroDBSagaStorage<TId, TSaga> : ISagaStorage<TId, TSaga>
    where TSaga : Saga
{
    private readonly IDocumentStore _store;

    public AeroDBSagaStorage(IDocumentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<TSaga?> LoadAsync(TId id, CancellationToken cancellationToken)
    {
        await using var session = await _store.QuerySessionAsync(cancellationToken);
        var idStr = id?.ToString();
        if (string.IsNullOrEmpty(idStr)) return null;

        var table = GetTableName(typeof(TSaga));
        var results = await session.RawQueryAsync<TSaga>(
            $"SELECT * FROM {table}:`{EscapeId(idStr)}`",
            null, cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task InsertAsync(TSaga saga, CancellationToken cancellationToken)
    {
        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        session.Store(saga);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(TSaga saga, CancellationToken cancellationToken)
    {
        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        session.Store(saga);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(TSaga saga, CancellationToken cancellationToken)
    {
        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, cancellationToken);
        session.Delete(saga);
        await session.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync() { }

    private static string GetTableName(Type sagaType) => ToSnakeCase(sagaType.Name);

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }

    private static string EscapeId(string id) => id.Replace("`", "``");
}
