namespace AeroDB.LiveQuery;

public interface ILiveQuerySession : IAsyncDisposable
{
    IDaliLiveQueryBuilder<T> Live<T>() where T : class;

    /// <summary>
    /// Raw SurrealQL escape hatch for queries the expression translator can't express.
    /// </summary>
    Task<IDaliLiveQuery<T>> LiveRawQuery<T>(
        string surql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T : class;
}
