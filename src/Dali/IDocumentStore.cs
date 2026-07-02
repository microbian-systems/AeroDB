using SurrealDb.Net;
using SurrealDb.Net.Models;
using Dali.Metadata;
using Dali.LiveQuery;

namespace Dali;

/// <summary>The primary entry point for the Dali document store. Provides access to query sessions, document sessions, event store, background daemon, and advanced SDK features.</summary>
public interface IDocumentStore : IAsyncDisposable
{
    Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a session dedicated to live queries (SurrealDB WebSocket required).
    /// Returns a fluent builder for typed, expression-based subscriptions.
    /// </summary>
    Task<ILiveQuerySession> LiveQuerySessionAsync(CancellationToken ct = default);

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
    /// <c>OpenSessionAsync</c>, or <c>OpenSessionAsync</c>.
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

    // ── Tenant-scoped BulkInsert overloads ──────────────────────

    /// <summary>Bulk insert typed documents for a specific tenant.</summary>
    Task BulkInsertAsync<T>(string tenantId, IEnumerable<T> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default) where T : class;

    /// <summary>Bulk insert mixed-type documents for a specific tenant.</summary>
    Task BulkInsertDocumentsAsync(string tenantId, IEnumerable<object> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default);

    /// <summary>Bulk insert mixed-type documents (non-tenant-scoped).</summary>
    Task BulkInsertDocumentsAsync(IEnumerable<object> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default);

    /// <summary>Bulk insert events across multiple streams for a specific tenant.</summary>
    Task BulkInsertEventsAsync(string tenantId, IEnumerable<(string StreamId, IEnumerable<object> Events)> streams, int batchSize = 1000, CancellationToken ct = default);

    // ── Tenant-scoped session factories ─────────────────────────

    /// <summary>Open an identity-tracking session scoped to a tenant.</summary>
    Task<IDocumentSession> IdentitySessionAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Open a dirty-tracked session scoped to a tenant.</summary>
    Task<IDocumentSession> DirtyTrackedSessionAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>A read-only session for querying documents. Supports LINQ queries, raw SurrealQL, load-by-ID, compiled queries, batch queries, live notifications, and graph traversal.</summary>
public interface IQuerySession : IAsyncDisposable
{
    Task<T?> LoadAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document by its integer ID.</summary>
    Task<T?> LoadAsync<T>(int id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document by its long ID.</summary>
    Task<T?> LoadAsync<T>(long id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document by its Guid ID.</summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document by an object ID (boxed).</summary>
    Task<T?> LoadAsync<T>(object id, CancellationToken ct = default) where T : class;

    /// <summary>Check whether a document with the given string ID exists.</summary>
    Task<bool> CheckExistsAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>Check whether a document with the given integer ID exists.</summary>
    Task<bool> CheckExistsAsync<T>(int id, CancellationToken ct = default) where T : class;

    /// <summary>Check whether a document with the given long ID exists.</summary>
    Task<bool> CheckExistsAsync<T>(long id, CancellationToken ct = default) where T : class;

    /// <summary>Check whether a document with the given Guid ID exists.</summary>
    Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken ct = default) where T : class;

    /// <summary>Check whether a document with the given object ID exists.</summary>
    Task<bool> CheckExistsAsync<T>(object id, CancellationToken ct = default) where T : class;

    /// <summary>Bulk load multiple documents by their string IDs.</summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default) where T : class;

    /// <summary>Bulk load multiple documents by their Guid IDs.</summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<Guid> ids, CancellationToken ct = default) where T : class;

    /// <summary>Bulk load multiple documents by their long IDs.</summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<long> ids, CancellationToken ct = default) where T : class;

    /// <summary>Bulk load multiple documents by their integer IDs.</summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<int> ids, CancellationToken ct = default) where T : class;

    /// <summary>Fetch metadata for a given entity instance.</summary>
    Task<IDocumentMetadata?> MetadataForAsync<T>(T entity, CancellationToken ct = default) where T : class;

    /// <summary>Set a per-session header for tracing/correlation.</summary>
    void SetHeader(string key, object value);

    /// <summary>Get a per-session header. Returns null if not found.</summary>
    object? GetHeader(string key);

    /// <summary>The underlying document store that created this session.</summary>
    IDocumentStore? DocumentStore { get; }

    /// <summary>
    /// Fetch the latest projected aggregate document for the given stream
    /// without replaying events. Returns null if no projected document exists.
    /// The stream ID must match the projected document ID (as used by
    /// <see cref="SingleStreamProjection{T}"/>).
    /// </summary>
    Task<T?> FetchLatest<T>(string streamId, CancellationToken ct = default) where T : class;

    /// <summary>Get the known version for a tracked entity. Returns the version at load/store time.</summary>
    long? VersionFor<T>(T entity) where T : class;

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
    /// Trace causation ID for distributed tracing — identifies which event/command
    /// caused this session to be opened.
    /// </summary>
    string? CausationId { get; set; }

    /// <summary>
    /// Trace correlation ID for grouping related operations across services.
    /// </summary>
    string? CorrelationId { get; set; }

    /// <summary>
    /// Optional session logger for Marten-compatible diagnostic recording.
    /// When set, all session operations are recorded through this logger.
    /// </summary>
    IMartenSessionLogger? Logger { get; set; }

    /// <summary>
    /// Number of database requests made in this session.
    /// </summary>
    int RequestCount { get; }

    /// <summary>
    /// Database metadata for the current session.
    /// </summary>
    IDatabase Database { get; }

    /// <summary>JSON document loader for raw JSON access.</summary>
    IJsonLoader Json { get; }

    /// <summary>
    /// [Marten API parity] Starts a live query monitoring a table for all changes (create/update/delete).
    /// Delegates to the new <see cref="Dali.LiveQuery.ILiveQuerySession"/> fluent API internally.
    /// Wraps the result in <see cref="ILiveQuery{T}"/> for Marten compatibility.
    /// Only works with WebSocket connections (ws://, wss://).
    /// </summary>
    Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class;

    /// <summary>
    /// [Marten API parity] Starts a live query with a custom SurrealQL WHERE clause filter.
    /// Delegates to <see cref="Dali.LiveQuery.ILiveQuerySession.LiveRawQuery{T}"/> internally.
    /// Only works with WebSocket connections (ws://, wss://).
    /// </summary>
    Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class;

    /// <summary>
    /// [Marten API parity] Watches events for a specific stream ID (mt_events table).
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

    /// <summary>Execute a query from a pre-generated execution plan string.</summary>
    Task<List<T>> QueryByPlanAsync<T>(string plan, CancellationToken ct = default) where T : class;

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

    /// <summary>
    /// Execute a compiled query and return the result as a JSON string.
    /// Returns null if no matching document is found.
    /// </summary>
    /// <param name="compiledQuery">The compiled query instance with parameter values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result type.</typeparam>
    Task<string?> ToJsonOne<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class;

    /// <summary>
    /// Execute a compiled list query and return results as a JSON array string.
    /// Returns an empty array "[]" if no results are found.
    /// </summary>
    /// <param name="compiledQuery">The compiled list query instance with parameter values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result element type.</typeparam>
    Task<string> ToJsonMany<TDoc, TOut>(ICompiledListQuery<TDoc, TOut> compiledQuery, CancellationToken ct = default)
        where TDoc : class;

    /// <summary>
    /// Execute a compiled query and write the JSON result directly to a stream.
    /// Nothing is written if no matching document is found.
    /// </summary>
    /// <param name="compiledQuery">The compiled query instance with parameter values.</param>
    /// <param name="destination">The destination stream to write JSON to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result type.</typeparam>
    Task StreamJsonOne<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery, Stream destination, CancellationToken ct = default)
        where TDoc : class;

    /// <summary>
    /// Execute a compiled list query and write the JSON array result directly to a stream.
    /// Writes an empty array "[]" if no results are found.
    /// </summary>
    /// <param name="compiledQuery">The compiled list query instance with parameter values.</param>
    /// <param name="destination">The destination stream to write JSON to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="TDoc">The document type being queried.</typeparam>
    /// <typeparam name="TOut">The result element type.</typeparam>
    Task StreamJsonMany<TDoc, TOut>(ICompiledListQuery<TDoc, TOut> compiledQuery, Stream destination, CancellationToken ct = default)
        where TDoc : class;

    // ── ITEM 2: StreamJson<T> ──────────────────────────────────────

    /// <summary>
    /// Execute raw SurrealQL and stream JSON results to a stream.
    /// </summary>
    /// <typeparam name="T">The document type to deserialize each result row.</typeparam>
    /// <param name="destination">The destination stream.</param>
    /// <param name="sql">The raw SurrealQL query.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StreamJson<T>(Stream destination, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Execute raw SurrealQL with a placeholder and stream JSON results to a stream.
    /// The placeholder is replaced with the session's database name before execution.
    /// </summary>
    /// <typeparam name="T">The document type to deserialize each result row.</typeparam>
    /// <param name="destination">The destination stream.</param>
    /// <param name="placeholder">The placeholder string to replace with the database name (e.g., <c>"{database}"</c>).</param>
    /// <param name="sql">The raw SurrealQL query with placeholder.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StreamJson<T>(Stream destination, string placeholder, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class;

    // ── ITEM 3: QueryAsync placeholder variant ─────────────────────

    /// <summary>
    /// Execute raw SurrealQL with a placeholder for tenant-scoped queries.
    /// The placeholder is replaced with the session's database name.
    /// Also replaces <c>{tenant}</c> with the session's tenant ID (if set).
    /// </summary>
    /// <typeparam name="T">The document type to deserialize each result row.</typeparam>
    /// <param name="placeholder">The placeholder string to replace (e.g., <c>"{database}"</c>).</param>
    /// <param name="sql">The raw SurrealQL query with placeholders.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T>> QueryAsync<T>(string placeholder, string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class;

    // ── ITEM 4: Advanced SQL addons ────────────────────────────────

    /// <summary>
    /// Execute raw SurrealQL and stream results as <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The document type to deserialize each result row.</typeparam>
    /// <param name="sql">The raw SurrealQL query.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<T> StreamAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Stream raw SurrealQL results as paired 2-tuples.
    /// Each yields a tuple pairing the first result from first statement with the first result from second statement, etc.
    /// </summary>
    IAsyncEnumerable<(T1, T2)> StreamAsync<T1, T2>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class;

    /// <summary>
    /// Stream raw SurrealQL results as 3-tuples.
    /// </summary>
    IAsyncEnumerable<(T1, T2, T3)> StreamAsync<T1, T2, T3>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class;

    /// <summary>
    /// Execute a raw multi-statement SurrealQL query returning two result types as paired tuples.
    /// Each tuple pairs the first result from the first statement with the first result from the second statement, etc.
    /// </summary>
    Task<List<(T1, T2)>> QueryAsync<T1, T2>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class;

    /// <summary>
    /// Execute a raw multi-statement SurrealQL query returning three result types as paired tuples.
    /// </summary>
    Task<List<(T1, T2, T3)>> QueryAsync<T1, T2, T3>(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class;

    // ── ITEM 4b: Search convenience methods (Marten parity) ────────

    /// <summary>Full-text search using SurrealDB analyzers. Requires a FULLTEXT index on the document type.</summary>
    /// <param name="searchTerm">The search query text.</param>
    /// <param name="analyzer">Optional SurrealDB analyzer name (e.g., "english", "simple"). Defaults to "simple".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="T">The document type to search.</typeparam>
    Task<IReadOnlyList<T>> SearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class;

    /// <summary>Plain-text search for documents containing all terms. Requires a FULLTEXT index.</summary>
    Task<IReadOnlyList<T>> PlainTextSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class;

    /// <summary>Phrase search for exact phrase match. Requires a FULLTEXT index.</summary>
    Task<IReadOnlyList<T>> PhraseSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class;

    /// <summary>Web-style search with fuzzy matching. Requires a FULLTEXT index.</summary>
    Task<IReadOnlyList<T>> WebStyleSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class;

    /// <summary>Prefix search for terms starting with the given prefix. Works without FULLTEXT indexes.</summary>
    Task<IReadOnlyList<T>> PrefixSearchAsync<T>(string searchTerm, string? analyzer = null, CancellationToken ct = default) where T : class;

    // ── ITEM 5: QueryForNonStaleData (Marten parity) ─────────────────

    /// <summary>Wait for the async daemon to process all events up to the current sequence, then return a queryable.</summary>
    /// <param name="timeout">Maximum time to wait for the daemon to catch up.</param>
    /// <typeparam name="T">The document type to query.</typeparam>
    Task<ISurrealDbQueryable<T>> QueryForNonStaleData<T>(TimeSpan timeout) where T : class;

    /// <summary>Wait for the async daemon with timeout mode control.</summary>
    /// <param name="timeout">Maximum time to wait for the daemon to catch up.</param>
    /// <param name="mode">Controls staleness tolerance: <see cref="StaleDataMode.Strict"/> waits, <see cref="StaleDataMode.AllowStale"/> returns immediately.</param>
    /// <typeparam name="T">The document type to query.</typeparam>
    Task<ISurrealDbQueryable<T>> QueryForNonStaleData<T>(TimeSpan timeout, StaleDataMode mode) where T : class;

    // ── ITEM: QueueSqlCommand (Marten parity) ──────────────────────────

    /// <summary>
    /// Queue a SQL command to be executed during <see cref="IDocumentSession.SaveChangesAsync"/>.
    /// The <paramref name="placeholder"/> is replaced with the session's database/tenant name before execution.
    /// </summary>
    /// <param name="placeholder">The placeholder string to replace with the database name (e.g. <c>"{database}"</c>).</param>
    /// <param name="sql">The raw SurrealQL command with placeholder.</param>
    /// <param name="parameters">Optional query parameters.</param>
    void QueueSqlCommand(string placeholder, string sql, params object[] parameters);
}

/// <summary>A read/write session that extends <see cref="IDocumentOperations"/> and <see cref="IQuerySession"/> with document storage (<c>Store</c>/<c>Delete</c>/<c>BulkInsert</c>), event sourcing (<c>Append</c>/<c>StartStream</c>), and unit-of-work change tracking.</summary>
public interface IDocumentSession : IDocumentOperations, IQuerySession
{
    /// <summary>
    /// Begins an explicit SurrealDB transaction. Returns an <see cref="IDaliTransaction"/> for
    /// explicit commit/rollback control. When active, <see cref="SaveChangesAsync"/> runs inside
    /// this transaction without auto-committing, supporting multiple <c>SaveChangesAsync</c> calls
    /// within a single transaction.
    /// <para>
    /// Alternative: use <see cref="CommitTransactionAsync"/> or <see cref="RollbackTransactionAsync"/>
    /// on the session directly.
    /// </para>
    /// </summary>
    IDaliTransaction BeginTransaction();

    /// <summary>
    /// Begins an explicit SurrealDB transaction asynchronously. Returns an <see cref="IDaliTransaction"/>
    /// for explicit commit/rollback control. When active, <see cref="SaveChangesAsync"/> runs inside
    /// this transaction without auto-committing, supporting multiple <c>SaveChangesAsync</c> calls
    /// within a single transaction.
    /// <para>
    /// Alternative: use <see cref="CommitTransactionAsync"/> or <see cref="RollbackTransactionAsync"/>
    /// on the session directly.
    /// </para>
    /// </summary>
    Task<IDaliTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Commits the current explicit transaction started by <see cref="BeginTransaction"/> or
    /// <see cref="BeginTransactionAsync"/>. Throws if no active transaction exists.
    /// </summary>
    Task CommitTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back / cancels the current explicit transaction started by <see cref="BeginTransaction"/> or
    /// <see cref="BeginTransactionAsync"/>. Throws if no active transaction exists.
    /// </summary>
    Task RollbackTransactionAsync(CancellationToken ct = default);

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
    new Task<int> BulkInsertAsync<T>(IEnumerable<T> documents, int batchSize = 100, CancellationToken ct = default) where T : class;

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
    new Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    /// <summary>The pending unit of work. Allows inspection of queued operations before commit.</summary>
    IUnitOfWork PendingChanges { get; }

    /// <summary>Remove a document from the identity map by the entity instance.</summary>
    void EjectById<T>(T document) where T : class;

    /// <summary>Remove a document from the identity map AND pending unit-of-work operations.</summary>
    void Eject<T>(T document) where T : class;

    /// <summary>Remove all documents of a given type from the identity map and pending operations.</summary>
    void EjectAllOfType(Type type);

    /// <summary>
    /// Switch to a specific tenant for multi-tenancy operations in this session.
    /// Returns the session for chaining.
    /// </summary>
    IDocumentSession ForTenant(string tenantId);

    /// <summary>
    /// Session-scoped listeners (in addition to store-level listeners registered
    /// via <c>StoreOptions.Listeners</c>). These are invoked in the same lifecycle
    /// hooks as store-level listeners.
    /// </summary>
    IList<IDocumentSessionListener> Listeners { get; }

    /// <summary>
    /// Per-session concurrency detection mode. When set to <see cref="ConcurrencyChecks.Disabled"/>,
    /// optimistic concurrency checks are skipped even if the store has
    /// <c>UseOptimisticConcurrency = true</c>. When <c>null</c>, the store default is used.
    /// </summary>
    ConcurrencyChecks? Concurrency { get; set; }

    /// <summary>
    /// Opens an identity-tracking session from the underlying store, pre-configured for the given tenant.
    /// </summary>
    Task<IDocumentSession> IdentitySessionForTenantAsync(string tenantId, CancellationToken ct = default);
}
