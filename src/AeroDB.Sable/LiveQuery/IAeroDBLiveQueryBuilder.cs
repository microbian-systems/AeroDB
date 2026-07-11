using System.Linq.Expressions;

namespace AeroDB.Sable.LiveQuery;

public interface IAeroDBLiveQueryBuilder<T> where T : class
{
    IAeroDBLiveQueryBuilder<T> Where(Expression<Func<T, bool>> predicate);

    IAeroDBLiveQueryBuilder<T> Select(params Expression<Func<T, object>>[] fields);

    IAeroDBLiveQueryBuilder<T> OnCreated(Action<T> handler);

    IAeroDBLiveQueryBuilder<T> OnUpdated(Action<T> handler);

    IAeroDBLiveQueryBuilder<T> OnDeleted(Action<T> handler);

    /// <summary>M3: Fires when the live query WebSocket connection opens.</summary>
    IAeroDBLiveQueryBuilder<T> OnOpen(Action handler);

    /// <summary>
    /// M2: Override the per-subscription channel capacity.
    /// Default: <see cref="StoreOptions.LiveQueryChannelCapacity"/> (4096).
    /// </summary>
    IAeroDBLiveQueryBuilder<T> ChannelCapacity(int capacity);

    Task<IAeroDBLiveQuery<T>> SubscribeAsync(CancellationToken ct = default);
}
