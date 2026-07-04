using System.Linq.Expressions;

namespace AeroDB.LiveQuery;

public interface IDaliLiveQueryBuilder<T> where T : class
{
    IDaliLiveQueryBuilder<T> Where(Expression<Func<T, bool>> predicate);

    IDaliLiveQueryBuilder<T> Select(params Expression<Func<T, object>>[] fields);

    IDaliLiveQueryBuilder<T> OnCreated(Action<T> handler);

    IDaliLiveQueryBuilder<T> OnUpdated(Action<T> handler);

    IDaliLiveQueryBuilder<T> OnDeleted(Action<T> handler);

    /// <summary>M3: Fires when the live query WebSocket connection opens.</summary>
    IDaliLiveQueryBuilder<T> OnOpen(Action handler);

    /// <summary>
    /// M2: Override the per-subscription channel capacity.
    /// Default: <see cref="StoreOptions.LiveQueryChannelCapacity"/> (4096).
    /// </summary>
    IDaliLiveQueryBuilder<T> ChannelCapacity(int capacity);

    Task<IDaliLiveQuery<T>> SubscribeAsync(CancellationToken ct = default);
}
