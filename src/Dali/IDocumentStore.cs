using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Dali;

/// <summary>The primary entry point for the Dali document store. Provides access to query sessions, document sessions, event store, background daemon, and advanced SDK features.</summary>
public interface IDocumentStore : IAsyncDisposable
{
    Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default);

    /// <summary>Open a session with full configuration control.</summary>
    Task<IDocumentSession> OpenSessionAsync(SessionOptions options, CancellationToken ct = default);

    [Obsolete("Use OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }) instead.")]
    Task<IDocumentSession> LightweightSessionAsync(CancellationToken ct = default);

    [Obsolete("Use OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly }) instead.")]
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

    /// <summary>Start a graph traversal query. Opens an ephemeral session internally.</summary>
    IGraphQuery<T> Graph<T>() where T : class;

    /// <summary>Advanced/low-level SDK access for scenarios Dali doesn't abstract.</summary>
    IDaliAdvanced Advanced { get; }

    /// <summary>
    /// Async daemon for background projection processing, if events are enabled
    /// and an <see cref="AsyncDaemon"/> has been started.
    /// </summary>
    AsyncDaemon? Daemon { get; }

    /// <summary>Execute a compiled query against a lightweight session.</summary>
    Task<TOut> QueryAsync<TDoc, TOut>(ICompiledQuery<TDoc, TOut> query, CancellationToken ct = default) where TDoc : class;

    /// <summary>Permanently deletes soft-deleted documents older than the specified age.</summary>
    Task<long> CleanDeletedDocumentsAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>A read-only session for querying documents. Supports LINQ queries, raw SurrealQL, load-by-ID, compiled queries, batch queries, live notifications, and graph traversal.</summary>
public interface IQuerySession : IAsyncDisposable
{
    Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Fetch the latest projected aggregate document for the given stream
    /// without replaying events. Returns null if no projected document exists.
    /// The stream ID must match the projected document ID (as used by
    /// <see cref="SingleStreamProjection{T}"/>).
    /// </summary>
    Task<T?> FetchLatest<T>(string streamId, CancellationToken ct = default) where T : class;
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

    /// <summary>
    /// The current user/identity for audit metadata (e.g., <see cref="IDocumentMetadata.LastModifiedBy"/>).
    /// Set this before <c>SaveChangesAsync</c> to populate <see cref="IDocumentMetadata.LastModifiedBy"/>
    /// via <see cref="Diagnostics.DocumentMetadataListener"/>.
    /// </summary>
    string? CurrentUser { get; set; }

    /// <summary>
    /// Starts a live query monitoring a table for all changes (create/update/delete).
    /// Only works with WebSocket connections (ws://, wss://).
    /// </summary>
    Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class;

    /// <summary>
    /// Starts a live query with a custom SurrealQL WHERE clause filter.
    /// Only works with WebSocket connections (ws://, wss://).
    /// </summary>
    Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Watches events for a specific stream ID (mt_events table).
    /// Only works with WebSocket connections (ws://, wss://).
    /// </summary>
    Task<ILiveQuery<object>> WatchStreamAsync(string streamId, CancellationToken ct = default);

    /// <summary>Start a graph traversal query. Uses SurrealDB's graph arrow syntax for edges and paths.</summary>
    IGraphQuery<T> Graph<T>() where T : class;

    /// <summary>
    /// Creates a batch query that can execute multiple compiled queries
    /// in a single SurrealDB multi-statement round trip.
    /// </summary>
    IBatchedQuery CreateBatchQuery();

    /// <summary>
    /// Executes a Marten-compatible interface-based compiled query.
    /// The query type implements <see cref="ICompiledQuery{TDoc,TOut}"/> and defines
    /// its parameterization via <see cref="ICompiledQuery{TDoc,TOut}.QueryIs"/>.
    /// </summary>
    /// <param name="compiledQuery">The compiled query instance with parameter values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result type.</typeparam>
    Task<TOut> QueryAsync<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class;
}

/// <summary>A read/write session that extends <see cref="IDocumentOperations"/> and <see cref="IQuerySession"/> with document storage (<c>Store</c>/<c>Delete</c>/<c>BulkInsert</c>), event sourcing (<c>Append</c>/<c>StartStream</c>), and unit-of-work change tracking.</summary>
public interface IDocumentSession : IDocumentOperations, IQuerySession
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    void ClearChanges();
    IEvents Events { get; }

    /// <summary>Total number of entities currently tracked in the identity map.</summary>
    int IdentityMapCount { get; }

    /// <summary>Remove a document from the identity map by ID. Does NOT delete from the database.</summary>
    void Eject<T>(string id) where T : class;

    /// <summary>Remove all documents of a given type from the identity map.</summary>
    void EjectAll<T>() where T : class;

    /// <summary>Remove ALL documents from the identity map.</summary>
    void EjectAll();

    /// <summary>Bulk-insert documents. More efficient than individual Store calls for large batches.</summary>
    Task<int> BulkInsertAsync<T>(IEnumerable<T> documents, int batchSize = 100, CancellationToken ct = default) where T : class;

    /// <summary>Queue a graph edge for creation during <see cref="SaveChangesAsync"/>. Executes inside the transaction.</summary>
    void Relate<TEdge>(
        RecordId from,
        RecordId to,
        TEdge? data = default) where TEdge : class;

    /// <summary>Queue a graph edge for deletion during <see cref="SaveChangesAsync"/>. Executes inside the transaction.</summary>
    void Unrelate(RecordId edgeId);

    /// <summary>
    /// Swappable per-session logger for diagnostics and recording.
    /// When set, all session operations log to this logger instead of the global factory.
    /// </summary>
    Microsoft.Extensions.Logging.ILogger? Logger { get; set; }
}
