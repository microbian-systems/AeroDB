namespace AeroDB.Sable;

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
    /// Execute a raw SurrealQL query and return results as a list of raw JSON strings.
    /// Useful for diagnostic queries or when the result shape is dynamic.
    /// </summary>
    Task<IReadOnlyList<string>> QueryRawAsync(string surql, CancellationToken ct = default);

    /// <summary>
    /// Executes all batched queries in a single round trip.
    /// Results are set on each future returned by <see cref="Query{TDoc,TOut}"/>.
    /// May be called multiple times — items accumulate additively.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task Execute(CancellationToken ct = default);

    // ── Marten API parity additions ────────────────────────────────

    /// <summary>Check if a document exists by ID.</summary>
    IBatchedQuery CheckExists<T>(string id, out Task<bool> result) where T : class;

    /// <summary>Load a document by string ID.</summary>
    IBatchedQuery Load<T>(string id, out Task<T?> result) where T : class;

    /// <summary>Load multiple documents by string IDs.</summary>
    IBatchedQuery LoadMany<T>(IEnumerable<string> ids, out Task<IReadOnlyList<T>> result) where T : class;

    /// <summary>Query using LINQ expression (full table select).</summary>
    IBatchedQuery Query<T>(out Task<IReadOnlyList<T>> result) where T : class;

    /// <summary>Add a custom handler for a result set.</summary>
    IBatchedQuery AddItem<T>(Func<IReadOnlyList<T>, Task> handler) where T : class;

    /// <summary>Query by an execution plan (raw SurrealQL).</summary>
    IBatchedQuery QueryByPlan<T>(string plan, out Task<IReadOnlyList<T>> result) where T : class;

    /// <summary>Batch event operations.</summary>
    IBatchEvents Events { get; }

    /// <summary>Parent query session that created this batch.</summary>
    IQuerySession Parent { get; }
}

/// <summary>
/// Batch event operations that can be queued alongside document queries
/// for execution in a single round trip.
/// </summary>
public interface IBatchEvents
{
    /// <summary>Queue fetching all events for a stream.</summary>
    IBatchedQuery FetchStream(string streamId, out Task<IReadOnlyList<IEvent>> result);

    /// <summary>Queue fetching all events after a given global sequence number.</summary>
    IBatchedQuery FetchAllAfterSequence(long sequence, out Task<IReadOnlyList<IEvent>> result);
}
