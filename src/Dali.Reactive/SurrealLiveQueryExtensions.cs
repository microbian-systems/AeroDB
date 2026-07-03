namespace Dali;

using System.Reactive.Linq;
using Dali.LiveQuery;

public static class SurrealLiveQueryExtensions
{
    /// <summary>
    /// Bridges the live query builder to an <see cref="IObservable{T}"/> of <see cref="DaliLiveChange{T}"/>.
    /// Each subscriber triggers a fresh <see cref="IDaliLiveQueryBuilder{T}.SubscribeAsync"/>
    /// call (deferred execution). The server-side query starts only when the first subscriber
    /// attaches. Unsubscribing disposes the underlying query.
    /// </summary>
    public static IObservable<DaliLiveChange<T>> ToObservable<T>(
        this IDaliLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return Observable.Create<DaliLiveChange<T>>(async (observer, innerCt) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, innerCt);
            var token = linkedCts.Token;

            var query = await builder.SubscribeAsync(token).ConfigureAwait(false);
            try
            {
                await foreach (var change in query.Changes(token).ConfigureAwait(false))
                {
                    observer.OnNext(change);
                }
            }
            finally
            {
                await query.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Wraps an existing <see cref="IDaliLiveQuery{T}"/> as an <see cref="IObservable{T}"/> of <see cref="DaliLiveChange{T}"/>.
    /// Caller owns the query lifecycle — the observable does not dispose the query.
    /// </summary>
    public static IObservable<DaliLiveChange<T>> ToObservable<T>(
        this IDaliLiveQuery<T> query,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        return Observable.Create<DaliLiveChange<T>>(async (observer, innerCt) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, innerCt);
            var token = linkedCts.Token;

            try
            {
                await foreach (var change in query.Changes(token).ConfigureAwait(false))
                {
                    observer.OnNext(change);
                }
                observer.OnCompleted();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Clean unsubscribe — subscriber detached
            }
        });
    }
}
