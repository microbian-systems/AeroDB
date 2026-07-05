namespace AeroDB;

using System.Reactive.Linq;
using AeroDB.LiveQuery;

public static class SurrealLiveQueryExtensions
{
    /// <summary>
    /// Bridges the live query builder to an <see cref="IObservable{T}"/> of <see cref="AeroDBLiveChange{T}"/>.
    /// Each subscriber triggers a fresh <see cref="IAeroDBLiveQueryBuilder{T}.SubscribeAsync"/>
    /// call (deferred execution). The server-side query starts only when the first subscriber
    /// attaches. Unsubscribing disposes the underlying query.
    /// </summary>
    public static IObservable<AeroDBLiveChange<T>> ToObservable<T>(
        this IAeroDBLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return Observable.Create<AeroDBLiveChange<T>>(async (observer, innerCt) =>
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
    /// Wraps an existing <see cref="IAeroDBLiveQuery{T}"/> as an <see cref="IObservable{T}"/> of <see cref="AeroDBLiveChange{T}"/>.
    /// Caller owns the query lifecycle — the observable does not dispose the query.
    /// </summary>
    public static IObservable<AeroDBLiveChange<T>> ToObservable<T>(
        this IAeroDBLiveQuery<T> query,
        CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        return Observable.Create<AeroDBLiveChange<T>>(async (observer, innerCt) =>
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

    /// <summary>
    /// Shortcut: <c>builder.ToObservable(ct).SelectOnCreate()</c>.
    /// Returns an observable of created documents only.
    /// </summary>
    public static IObservable<T> OnCreate<T>(
        this IAeroDBLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        return builder.ToObservable(ct).SelectOnCreate();
    }

    /// <summary>
    /// Shortcut: <c>builder.ToObservable(ct).SelectOnUpdate()</c>.
    /// Returns an observable of updated documents only.
    /// </summary>
    public static IObservable<T> OnUpdate<T>(
        this IAeroDBLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        return builder.ToObservable(ct).SelectOnUpdate();
    }

    /// <summary>
    /// Shortcut: <c>builder.ToObservable(ct).SelectOnDelete()</c>.
    /// Returns an observable of deleted documents only.
    /// </summary>
    public static IObservable<T> OnDelete<T>(
        this IAeroDBLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        return builder.ToObservable(ct).SelectOnDelete();
    }

    /// <summary>
    /// Shortcut: <c>builder.ToObservable(ct).SelectResults()</c>.
    /// Returns an observable of all results EXCEPT Close events.
    /// </summary>
    public static IObservable<AeroDBLiveChange<T>> Results<T>(
        this IAeroDBLiveQueryBuilder<T> builder,
        CancellationToken ct = default) where T : class
    {
        return builder.ToObservable(ct).SelectResults();
    }
}
