namespace Dali;

/// <summary>
/// A batch of compiled queries that are executed together in a single
/// SurrealDB multi-statement round trip (Marten-compatible pattern).
///
/// Usage:
/// <code>
/// var batch = session.CreateBatchQuery();
/// var justin = batch.Query(new FindByFirstName { FirstName = "Justin" });
/// var tamba = batch.Query(new FindByFirstName { FirstName = "Tamba" });
/// await batch.Execute();
/// var user1 = await justin;
/// var user2 = await tamba;
/// </code>
/// </summary>
public interface IBatchedQuery
{
    /// <summary>
    /// Adds a compiled query to this batch. Returns an awaitable "future"
    /// that will contain the result after <see cref="Execute"/> completes.
    /// </summary>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result type.</typeparam>
    /// <param name="compiledQuery">The compiled query instance with parameter values.</param>
    Task<TOut> Query<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery)
        where TDoc : class;

    /// <summary>
    /// Executes all batched queries in a single round trip.
    /// Results are set on each future returned by <see cref="Query{TDoc,TOut}"/>.
    /// May be called multiple times — items accumulate additively.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task Execute(CancellationToken ct = default);
}
