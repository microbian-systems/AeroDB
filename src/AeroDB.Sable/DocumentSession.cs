using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AeroDB.Sable.Internals.Cbor;
using AeroDB.Sable.LiveQuery;
using AeroDB.Sable.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

/// <summary>The concrete document session implementing <see cref="IDocumentSession"/>. Manages a unit-of-work with automatic change tracking, identity map, event appending, and transactional save via SurrealDB.</summary>
public class DocumentSession : InternalSessionBase, IDocumentSession
{
    private static readonly JsonSerializerOptions DefaultLiteralSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private ILogger<DocumentSession> _baseLogger;
    private Microsoft.Extensions.Logging.ILogger? _loggerOverride;
    private readonly UnitOfWork _unitOfWork = new();

    private ILogger ResolvedLogger => _loggerOverride ?? _baseLogger;

    /// <inheritdoc />
    public new Microsoft.Extensions.Logging.ILogger? Logger
    {
        get => _loggerOverride;
        set => _loggerOverride = value;
    }

    /// <summary>
    /// Explicit implementation of <see cref="IQuerySession.Logger"/> to resolve the
    /// naming conflict with <see cref="IDocumentSession.Logger"/>. Delegates to the
    /// <see cref="InternalSessionBase"/> property.
    /// </summary>
    IMartenSessionLogger? IQuerySession.Logger
    {
        get => base.Logger;
        set => base.Logger = value;
    }
    private readonly SessionOptions? _sessionOptions;
    private IEvents? _events;
    internal readonly List<IEvent> _appendedEvents = new();
    internal readonly List<IDeferredPatch> _queuedPatches = new();
    internal readonly List<QueuedRelation> _queuedRelations = new();
    internal readonly List<RecordId> _queuedUnrelations = new();
    private readonly List<QueuedRelation> _transactionRelations = new();
    private readonly List<RecordId> _transactionUnrelations = new();
    internal readonly List<IFetchForWritingResult> _fetchForWritingResults = new();
    private ConcurrencyChecks? _concurrencyOverride;

    /// <summary>
    /// The current explicit transaction, if any. Set by <see cref="BeginTransaction"/> /
    /// <see cref="BeginTransactionAsync"/>. When non-null, <see cref="SaveChangesAsync"/>
    /// runs inside this transaction without auto-committing.
    /// </summary>
    private SurrealDbTransaction? _explicitTransaction;

    /// <summary>
    /// Whether this session owns the explicit transaction and is responsible for
    /// cleaning it up on dispose.
    /// </summary>
    private bool _ownsTransaction;

    /// <summary>
    /// Whether the store-level <c>UseOptimisticConcurrency</c> is active for this session,
    /// respecting any per-session override set via <see cref="Concurrency"/>.
    /// </summary>
    internal protected override bool UseOptimisticConcurrency =>
        _concurrencyOverride.HasValue
            ? _concurrencyOverride.Value == ConcurrencyChecks.Enabled
            : Options.UseOptimisticConcurrency;

    /// <summary>
    /// Cached <c>MethodInfo</c> for <see cref="SurrealDbResponse.GetValue{T}"/>,
    /// used by <see cref="CheckConcurrencyAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? GetValueMethod = typeof(SurrealDbResponse).GetMethods()
        .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition);

    /// <summary>
    /// Cached <c>MethodInfo</c> for <c>ISurrealDbSession.Create&lt;T&gt;</c>,
    /// used by <see cref="CreateEntityAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? CreateMethod = typeof(ISurrealDbSession).GetMethods()
        .FirstOrDefault(m => m.Name == nameof(ISurrealDbSession.Create)
            && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(string)
            && m.GetParameters()[2].ParameterType == typeof(CancellationToken));

    /// <summary>
    /// Cached <c>MethodInfo</c> for <c>ISurrealDbSharedMethods.Upsert&lt;T, T&gt;</c>,
    /// used by <see cref="UpsertRecordAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? UpsertMethod = typeof(ISurrealDbSharedMethods).GetMethods()
        .First(m => m.Name == nameof(ISurrealDbSharedMethods.Upsert)
            && m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(RecordId));

    private static readonly MethodInfo? RelateMethod = typeof(ISurrealDbSharedMethods).GetMethods()
        .First(m => m.Name == nameof(ISurrealDbSharedMethods.Relate)
            && m.IsGenericMethodDefinition
            && m.GetGenericArguments().Length == 2
            && m.GetParameters().Length == 5
            && m.GetParameters()[0].ParameterType == typeof(string)
            && m.GetParameters()[1].ParameterType == typeof(RecordId)
            && m.GetParameters()[2].ParameterType == typeof(RecordId)
            && m.GetParameters()[4].ParameterType == typeof(CancellationToken));

    public DocumentSession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, DocumentTracking tracking)
        : base(client, session, options, tracking)
    {
        _baseLogger = CreateLogger<DocumentSession>();
    }

    /// <summary>
    /// Creates a session with full <see cref="SessionOptions"/>, wiring session-level
    /// listeners and policies on top of store-level configuration.
    /// </summary>
    internal DocumentSession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, SessionOptions sessionOptions)
        : base(client, session, options, sessionOptions.Tracking)
    {
        _baseLogger = CreateLogger<DocumentSession>();
        _sessionOptions = sessionOptions;

        // Wire session-level listeners (added after store-level listeners)
        if (sessionOptions.Listeners is { Count: > 0 })
        {
            SessionListeners.AddRange(sessionOptions.Listeners);
        }

        // Apply session-level document policies on top of store-level policies
        if (sessionOptions.Policies is { Count: > 0 })
        {
            foreach (var mapping in Options.Schema.Mappings.Values)
            {
                foreach (var policy in sessionOptions.Policies)
                    policy.Apply(mapping);
            }
        }
    }

    /// <summary>
    /// Begins a database transaction synchronously.
    /// Prefer <see cref="BeginTransactionAsync"/> in ASP.NET contexts to avoid sync-over-async deadlock.
    /// </summary>
    public IAeroDBTransaction BeginTransaction()
    {
        if (_explicitTransaction != null)
            throw new InvalidOperationException("A transaction is already in progress.");

        _explicitTransaction = Session.BeginTransaction(DefaultCt).GetAwaiter().GetResult();
        _ownsTransaction = true;
        return new AeroDBTransaction(_explicitTransaction, this);
    }

    /// <summary>
    /// Begins an explicit SurrealDB transaction asynchronously. Returns an <see cref="IAeroDBTransaction"/>
    /// for explicit commit/rollback control. When active, <see cref="SaveChangesAsync"/> runs inside
    /// this transaction without auto-committing, supporting multiple <c>SaveChangesAsync</c> calls
    /// within a single transaction.
    /// <para>
    /// Alternative: use <see cref="CommitTransactionAsync"/> or <see cref="RollbackTransactionAsync"/>
    /// on the session directly.
    /// </para>
    /// </summary>
    public async Task<IAeroDBTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_explicitTransaction != null)
            throw new InvalidOperationException("A transaction is already in progress.");

        _explicitTransaction = await Session.BeginTransaction(ct).ConfigureAwait(false);
        _ownsTransaction = true;
        return new AeroDBTransaction(_explicitTransaction, this);
    }

    /// <summary>
    /// Commits the current explicit transaction started by <see cref="BeginTransaction"/> or
    /// <see cref="BeginTransactionAsync"/>. Throws if no active transaction exists.
    /// </summary>
    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (_explicitTransaction == null)
            throw new InvalidOperationException("No active transaction to commit.");

        await FlushPendingGraphOperationsAsync(ct).ConfigureAwait(false);
        await _explicitTransaction.Commit(ct).ConfigureAwait(false);
        await _explicitTransaction.DisposeAsync().ConfigureAwait(false);
        _explicitTransaction = null;
        _ownsTransaction = false;
    }

    /// <summary>
    /// Rolls back / cancels the current explicit transaction started by <see cref="BeginTransaction"/> or
    /// <see cref="BeginTransactionAsync"/>. Throws if no active transaction exists.
    /// </summary>
    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_explicitTransaction == null)
            throw new InvalidOperationException("No active transaction to rollback.");

        await RollbackTransactionCoreAsync(ct).ConfigureAwait(false);
    }

    internal async Task RollbackTransactionIfActiveAsync(CancellationToken ct = default)
    {
        if (_explicitTransaction == null)
            return;

        await RollbackTransactionCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task RollbackTransactionCoreAsync(CancellationToken ct)
    {
        var transaction = _explicitTransaction;
        if (transaction == null)
            return;

        _queuedRelations.Clear();
        _queuedUnrelations.Clear();
        _transactionRelations.Clear();
        _transactionUnrelations.Clear();
        await transaction.Cancel(ct).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        _explicitTransaction = null;
        _ownsTransaction = false;
    }

    /// <summary>
    /// Called by <see cref="AeroDBTransaction"/> after commit/rollback to clear the session's
    /// transaction state and return to auto-transact mode.
    /// </summary>
    internal void ClearTransaction()
    {
        _explicitTransaction = null;
        _ownsTransaction = false;
    }

    public override IEvents Events
    {
        get
        {
            if (_events is null)
            {
                var inner = new EventStore(Session, Options);
                _events = new TrackingEventStore(inner, this);
            }
            return _events;
        }
    }

    /// <summary>
    /// Routes <see cref="ProjectionLifecycle.Live"/> projections through event replay
    /// instead of document storage lookup.
    /// </summary>
    public override async Task<T?> FetchLatest<T>(string streamId, CancellationToken ct = default) where T : class
    {
        foreach (var projection in Options.Projections)
        {
            if (projection.Lifecycle != ProjectionLifecycle.Live)
                continue;

            var projType = projection.GetType();
            while (projType is not null)
            {
                if (projType.IsGenericType && projType.GetGenericArguments().FirstOrDefault() == typeof(T))
                {
                    return await Events.AggregateAsync<T>(streamId, ct).ConfigureAwait(false);
                }
                projType = projType.BaseType;
            }
        }

        return await base.FetchLatest<T>(streamId, ct).ConfigureAwait(false);
    }

    public void Store<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequestCount++;

        // If conjoined tenancy is active and the entity has a TenantId property, set it
        // DatabasePerTenant isolates at the database level — no entity-level tenant ID needed.
        if (!string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
        {
            var meta = MetadataRegistry.TryGet<T>();
            if (meta is not null)
            {
                meta.SetTenantId(entity, TenantId);
            }
            else
            {
                var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
                if (tenantProp is not null && tenantProp.CanWrite)
                    tenantProp.SetValue(entity, TenantId);
            }
        }

        // Track original version for optimistic concurrency
        if (UseOptimisticConcurrency)
            TrackOriginalVersion(entity);

        // Populate identity map when identity tracking is enabled
        if (ShouldTrackInIdentityMap(typeof(T)))
        {
            var id = GetEntityId(entity);
            if (id is not null)
            {
                var typeMap = IdentityMap.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
                typeMap[id] = entity;
                if (IsDirtyTracking)
                    CaptureSnapshot(typeof(T), id, entity);
            }
        }

        _unitOfWork.Add(entity, OperationType.Added);
        ResolvedLogger.LogDebug("Stored {Type} for {Operation}", typeof(T).Name, OperationType.Added);
    }

    /// <summary>
    /// Adds an operation directly to the unit of work with a specified type.
    /// Internal for testing — use <c>Store&lt;T&gt;</c> or <c>Delete&lt;T&gt;</c> in production.
    /// </summary>
    internal void AddOperation<T>(T entity, OperationType type) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        _unitOfWork.Add(entity, type);
    }

    /// <summary>
    /// Tracks the version of an entity for optimistic concurrency checking.
    /// Called automatically by <c>Store&lt;T&gt;</c> and <see cref="InternalSessionBase.LoadAsync{T}"/>.
    /// This method is available for cases where entities are obtained via other means (e.g., Query)
    /// and their version needs to be captured for subsequent concurrency checks.
    /// </summary>
    internal void TrackVersion<T>(T entity) where T : class
    {
        if (UseOptimisticConcurrency)
            TrackOriginalVersion(entity);
    }

    public void Delete<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequestCount++;

        // If conjoined tenancy is active, validate tenant ownership
        // DatabasePerTenant isolates at the database level — no entity-level tenant check needed.
        if (!string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
        {
            string? entityTenant;
            var meta = MetadataRegistry.TryGet<T>();
            if (meta is not null)
            {
                entityTenant = meta.GetTenantId(entity);
            }
            else
            {
                var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
                entityTenant = tenantProp?.GetValue(entity) as string;
            }
            
            if (!string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot delete entity belonging to tenant '{entityTenant}' " +
                    $"from a session scoped to tenant '{TenantId}'.");
            }
        }

        // If the entity implements ISoftDeleted, queue a soft-delete instead
        if (entity is ISoftDeleted)
        {
            _unitOfWork.Add(entity, OperationType.SoftDeleted);
            ResolvedLogger.LogDebug("Queued {Type} for soft-deletion", typeof(T).Name);
        }
        else
        {
            _unitOfWork.Add(entity, OperationType.Deleted);
            ResolvedLogger.LogDebug("Queued {Type} for deletion", typeof(T).Name);
        }
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(CancellationToken ct = default) where T : class
    {
        return await Query<T>().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a document with an explicit string ID. Sets the entity's Id property before storing.
    /// Uses reflection to set the Id since metadata accessors may not always be available.
    /// </summary>
    public void Store<T>(string id, T document) where T : class
    {
        var idProp = typeof(T).GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (idProp is not null && idProp.CanWrite)
        {
            // Convert string id to the property type (string, RecordIdOf<string>, long, etc.)
            var propType = idProp.PropertyType;
            object convertedId = id;
            if (propType == typeof(long))
                convertedId = long.TryParse(id, out var l) ? l : 0;
            else if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
                convertedId = Activator.CreateInstance(propType, id)!;
            idProp.SetValue(document, convertedId);
        }
        Store(document);
    }

    /// <summary>
    /// Deletes all documents of type T matching the predicate using a raw SurrealQL DELETE query.
    /// Executes immediately. The predicate is used to build a SurrealQL WHERE clause.
    /// </summary>
    public async Task<long> DeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T), Options.Schema);
        // Use a basic field-value extraction for common equality predicates.
        // For complex predicates, callers should use RawQueryAsync or Query<T> + manual delete.
        var whereClause = BuildWhereClause(predicate);
        var sql = $"DELETE FROM {table} WHERE {whereClause};";
        var response = await Session.RawQuery(sql, null, ct).ConfigureAwait(false);
        return response.Count;
    }

    private string FormatWhereValue(object? value) => value switch
    {
        null => "NONE",
        string s => $"'{s.Replace("'", "\\'")}'",
        bool b => b ? "true" : "false",
        int or long or short or byte or sbyte or ushort or uint or ulong
            or float or double or decimal => value.ToString()!,
        Enum e => Options.EnumStorage == EnumStorage.AsString
            ? $"'{e}'"
            : Convert.ToInt64(e).ToString(),
        DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
        DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
        _ => $"'{value}'"
    };

    private string BuildWhereClause<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
    {
        // Simple binary expression handler: field == value
        if (predicate.Body is System.Linq.Expressions.BinaryExpression binary
            && binary.NodeType == System.Linq.Expressions.ExpressionType.Equal
            && binary.Left is System.Linq.Expressions.MemberExpression member
            && binary.Right is System.Linq.Expressions.ConstantExpression constant)
        {
            var fieldName = MetadataDispatch.GetFieldName(typeof(T), member.Member.Name, Options.Schema);
            return $"{fieldName} = {FormatWhereValue(constant.Value)}";
        }
        // Fallback: return a tautology (matches everything)
        return "true";
    }

    public Task<int> BulkInsertAsync<T>(IEnumerable<T> documents, int batchSize = 100, CancellationToken ct = default) where T : class
    {
        var list = documents as IReadOnlyList<T> ?? documents.ToList();
        return BulkOperations.BulkInsertAsync(this, list, batchSize, ct);
    }

    public void ClearChanges()
    {
        _unitOfWork.Clear();
        _appendedEvents.Clear();
        _queuedPatches.Clear();
        _queuedRelations.Clear();
        _queuedUnrelations.Clear();
        _transactionRelations.Clear();
        _transactionUnrelations.Clear();
        _queuedStorageOperations.Clear();
        QueuedSqlCommands.Clear();
        _expectedVersions.Clear();
        _expectedRevisions.Clear();
        _tryUpdateRevisions.Clear();
        ClearSnapshots();
        EjectAll();
    }

    public async Task<int> SaveChangesAsync(CancellationToken token = default)
    {
        var ct = token;
        RequestCount++;
        var count = _unitOfWork.Operations.Count;
        if (count == 0 && _appendedEvents.Count == 0 && _queuedPatches.Count == 0
            && _queuedRelations.Count == 0 && _queuedUnrelations.Count == 0
            && _fetchForWritingResults.Count == 0
            && _queuedStorageOperations.Count == 0 && QueuedSqlCommands.Count == 0) return 0;

        ResolvedLogger.LogInformation("SaveChangesAsync: committing {EntityCount} entities and {EventCount} events",
            count, _appendedEvents.Count);

        // Snapshots for IChangeSet in AfterCommitAsync
        var committedOperations = _unitOfWork.Operations.ToArray();
        (string StreamId, object Event)[] appendedEventSnapshot = [];
        int graphOpCount = 0;

        // Cross-DB check: group operations by their database target.
        // SurrealDB cannot span multiple databases in a single transaction,
        // so we reject cross-database batches up front.
        // This check must happen BEFORE the try/catch so the exception is not wrapped.
        var dbGroups = _unitOfWork.Operations
            .GroupBy(op => MetadataDispatch.GetSchemaTarget(op.EntityType, Options.Schema).Database)
            .ToList();

        if (dbGroups.Count > 1)
        {
            var dbNames = string.Join(", ",
                dbGroups.Select(g => $"'{g.Key ?? Options.Database ?? "test"}'"));
            throw new InvalidOperationException(
                $"Cross-database transactions are not supported. " +
                $"Unit of work spans multiple databases: {dbNames}");
        }

        // Resolve the target session for this database (null = default database)
        var targetSchemaName = dbGroups.Count > 0 ? dbGroups[0].Key : null;

        // Unrelation validation: RecordId table names embed their own database routing,
        // so cross-DB unrelation is verified at the SurrealDB level. We do not
        // resolve RecordId tables to schemas here to avoid meta-recursion.

        // Validate queued relation databases against the unit-of-work target
        if (_queuedRelations.Count > 0)
        {
            var relTargets = _queuedRelations
                .Select(r => MetadataDispatch.GetSchemaTarget(r.EdgeType, Options.Schema).Database)
                .Distinct()
                .ToList();
            if (relTargets.Count > 1)
            {
                var relDbNames = string.Join(", ", relTargets.Select(d => $"'{d ?? Options.Database ?? "test"}'"));
                throw new InvalidOperationException(
                    $"Cross-database graph operations are not supported. Queued relations span multiple databases: {relDbNames}");
            }
            var relTarget = relTargets[0];
            if (relTarget != targetSchemaName)
            {
                throw new InvalidOperationException(
                    $"Cross-database operations are not supported. Documents target '{targetSchemaName}', but queued relations target '{relTarget}'.");
            }
        }

        try
        {
            // BeforeSaveChangesAsync hooks (store + session level)
            if (Options.Listeners.Count > 0)
            {
                foreach (var listener in Options.Listeners)
                    await listener.BeforeSaveChangesAsync(this, ct).ConfigureAwait(false);
            }
            if (SessionListeners.Count > 0)
            {
                foreach (var listener in SessionListeners)
                    await listener.BeforeSaveChangesAsync(this, ct).ConfigureAwait(false);
            }

            var targetSession = await GetSessionForSchemaAsync(targetSchemaName, ct).ConfigureAwait(false);

            // Begin SurrealDB transaction — all per-entity operations on this session
            // participate because they share the underlying connection.
            SurrealDbTransaction? tx = null;
            bool ownsTx = false;

            if (_explicitTransaction != null)
            {
                // Use the explicit transaction — caller manages commit/rollback.
                // Operations run inside the explicit transaction scope.
                // Do NOT commit/cancel at the end — caller will do it.
            }
            else
            {
                tx = await targetSession.BeginTransaction(ct).ConfigureAwait(false);
                ownsTx = true;
            }

            try
            {
                // Phase 1: Optimistic concurrency checks (Modified entities only)
                // Runs before any mutations so we fail-fast if a conflict exists.
                HashSet<object>? revisionSkipOps = null;
                if ((UseOptimisticConcurrency || _expectedVersions.Count > 0 || _expectedRevisions.Count > 0) && count > 0)
                {
                    revisionSkipOps = new HashSet<object>();
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type == OperationType.Modified)
                        {
                            // Standard optimistic concurrency check
                            if (UseOptimisticConcurrency)
                                await CheckConcurrencyAsync(op, targetSession, ct).ConfigureAwait(false);

                            // Expected version check (UpdateExpectedVersion)
                            if (_expectedVersions.TryGetValue(op.Entity, out var expectedVer))
                            {
                                var dbVersion = await FetchVersionAsync(op.Entity, op.EntityType, targetSession, ct).ConfigureAwait(false);
                                if (expectedVer != dbVersion)
                                {
                                    ResolvedLogger.LogWarning(
                                        "Expected version mismatch on {Type} (id={Id}): expected {Expected}, found {Actual}",
                                        op.EntityType.Name, GetEntityId(op.Entity), expectedVer, dbVersion);
                                    throw new ConcurrencyException(op.EntityType, GetEntityId(op.Entity) ?? "?", expectedVer, dbVersion);
                                }
                            }

                            // Expected revision check (UpdateRevision / TryUpdateRevision)
                            if (_expectedRevisions.TryGetValue(op.Entity, out var expectedRev))
                            {
                                var dbVersion = await FetchVersionAsync(op.Entity, op.EntityType, targetSession, ct).ConfigureAwait(false);
                                if (expectedRev != dbVersion)
                                {
                                    if (_tryUpdateRevisions.Contains(op.Entity))
                                    {
                                        // Skip this entity — don't throw, just leave it out
                                        revisionSkipOps.Add(op.Entity);
                                        ResolvedLogger.LogDebug(
                                            "TryUpdateRevision: revision mismatch on {Type} (id={Id}), skipping",
                                            op.EntityType.Name, GetEntityId(op.Entity));
                                    }
                                    else
                                    {
                                        ResolvedLogger.LogWarning(
                                            "Revision mismatch on {Type} (id={Id}): expected {Expected}, found {Actual}",
                                            op.EntityType.Name, GetEntityId(op.Entity), expectedRev, dbVersion);
                                        throw new ConcurrencyException(op.EntityType, GetEntityId(op.Entity) ?? "?", expectedRev, dbVersion);
                                    }
                                }
                            }
                        }
                    }
                }

                // Phase 1.5: Pre-compute clean ops for dirty-tracking.
                // Must run BEFORE Phase 2 (version increment) so skipped entities
                // don't get an in-memory version bump.
                HashSet<Operation>? cleanOps = null;
                if (IsDirtyTracking && count > 0)
                {
                    cleanOps = new HashSet<Operation>();
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type == OperationType.Modified)
                        {
                            var opId = GetEntityId(op.Entity);
                            if (opId is not null && !HasChanged(op.EntityType, opId, op.Entity))
                            {
                                cleanOps.Add(op);
                                ResolvedLogger.LogDebug("Skipping {Type} {Id}: no changes detected (dirty tracking)", op.EntityType.Name, opId);
                            }
                        }
                    }
                }

                // Phase 2: Increment version fields on all entities before persisting.
                // Skip clean dirty-tracked ops (handled in Phase 1.5).
                if (UseOptimisticConcurrency && count > 0)
                {
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type is OperationType.Added or OperationType.Modified
                            && (cleanOps is null || !cleanOps.Contains(op)))
                            IncrementVersion(op.Entity);
                    }
                }

                // Phase 3: persist tracked entities (Added / Modified / Deleted)
                if (count > 0)
                {
                    foreach (var op in _unitOfWork.Operations)
                    {
                        // Dirty-tracking: skip clean modified entities (pre-computed in Phase 1.5)
                        if (cleanOps?.Contains(op) == true)
                            continue;

                        // TryUpdateRevision: skip entities whose revision didn't match
                        if (revisionSkipOps?.Contains(op.Entity) == true)
                            continue;

                        var table = MetadataDispatch.GetTableName(op.EntityType);

                        // Call before-store/before-delete listeners (after dirty-tracking skip)
                        if (Options.Listeners.Count > 0)
                        {
                            foreach (var listener in Options.Listeners)
                            {
                                if (op.Type is OperationType.Added or OperationType.Modified or OperationType.Insert or OperationType.Update)
                                    await listener.BeforeStoreAsync(this, op.Entity, ct).ConfigureAwait(false);
                                else if (op.Type is OperationType.Deleted or OperationType.SoftDeleted)
                                    await listener.BeforeDeleteAsync(this, op.Entity, ct).ConfigureAwait(false);
                            }
                        }
                        if (SessionListeners.Count > 0)
                        {
                            foreach (var listener in SessionListeners)
                            {
                                if (op.Type is OperationType.Added or OperationType.Modified or OperationType.Insert or OperationType.Update)
                                    await listener.BeforeStoreAsync(this, op.Entity, ct).ConfigureAwait(false);
                                else if (op.Type is OperationType.Deleted or OperationType.SoftDeleted)
                                    await listener.BeforeDeleteAsync(this, op.Entity, ct).ConfigureAwait(false);
                            }
                        }

                        switch (op.Type)
                        {
                            case OperationType.Added:
                                ResolvedLogger.LogDebug("UPSERT {Type} ({Table})", op.EntityType.Name, table);
                                // Fast path: entity has a typed RecordId — preserve it
                                if (op.Entity is IRecord recAdded && recAdded.Id is not null)
                                {
                                    await UpsertRecordAsync(recAdded, recAdded.Id, targetSession, ct).ConfigureAwait(false);
                                    break;
                                }
                                var entityId = GetEntityId(op.Entity);
                                if (!string.IsNullOrEmpty(entityId))
                                {
                                    // Auto-generate Guid for Guid.Empty identity
                                    var type = op.Entity.GetType();
                                    var mp = Options.Schema.Mappings.GetValueOrDefault(type);
                                    var idpName = mp?.IdentityProperty ?? "Id";
                                    var idp = type.GetProperty(idpName);
                                    if (idp is not null && idp.PropertyType == typeof(Guid) && idp.GetValue(op.Entity) is Guid guidVal && guidVal == Guid.Empty)
                                    {
                                        var newGuid = Guid.NewGuid();
                                        idp.SetValue(op.Entity, newGuid);
                                        entityId = newGuid.ToString();
                                    }

                                    // Use Upsert (create-or-update) for entities with explicit IDs.
                                    // This avoids failure when the record already exists (e.g. from
                                    // inline projections run in a prior session, or RebuildAsync).
                                    var rid = new RecordIdOf<string>(table, entityId);
                                    if (op.Entity is IRecord rec)
                                    {
                                        await UpsertRecordAsync(rec, rid, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // Non-IRecord (Entity<TId>) types with explicit IDs — use SurrealQL to avoid CBOR serialization issues
                                        if (TryBuildSurrealQlObjectLiteral(op.Entity, out var literal))
                                        {
                                            var response = await ExecuteRawWriteAsync(
                                                targetSession,
                                                $"UPSERT {table}:`{entityId}` CONTENT {literal}",
                                                null,
                                                ct).ConfigureAwait(false);
                                            ThrowIfRawQueryFailed(response);
                                        }
                                        else
                                        {
                                            var response = await ExecuteRawWriteAsync(
                                                targetSession,
                                                $"UPSERT {table}:`{entityId}` MERGE $data",
                                                new Dictionary<string, object?> { ["data"] = op.Entity },
                                                ct).ConfigureAwait(false);
                                            ThrowIfRawQueryFailed(response);
                                        }
                                    }
                                }
                                else
                                {
                                    // Use typed Create via reflection for proper RecordId population
                                    var createdResult = await CreateEntityAsync(op, table, targetSession, ct).ConfigureAwait(false);
                                    if (createdResult is not null)
                                    {
                                        // Try IRecord interface first (for Record-based types like Person)
                                        if (op.Entity is IRecord origRec && createdResult is IRecord createdRec && createdRec.Id is not null)
                                            origRec.Id = createdRec.Id;
                                        else
                                        {
                                            // Fallback: try reflection-based property copy
                                            var idProp = op.EntityType.GetProperty("Id");
                                            var createdId = createdResult.GetType().GetProperty("Id")?.GetValue(createdResult);
                                            if (idProp is not null && createdId is not null)
                                                idProp.SetValue(op.Entity, createdId);
                                        }
                                    }
                                }
                                break;

                            case OperationType.Modified:
                                ResolvedLogger.LogDebug("UPDATE {Type} ({Table})", op.EntityType.Name, table);
                                // Fast path: entity has a typed RecordId — preserve it
                                if (op.Entity is IRecord recMod && recMod.Id is not null)
                                {
                                    await UpsertRecordAsync(recMod, recMod.Id, targetSession, ct).ConfigureAwait(false);
                                    break;
                                }
                                var modId = GetRecordId(op.Entity, table);
                                if (modId is not null)
                                {
                                    if (op.Entity is IRecord record)
                                    {
                                        // Use Upsert (create-or-update) via the SDK's typed path,
                                        // which avoids CBOR serialization issues with JsonElement values.
                                        await UpsertRecordAsync(record, modId, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // Fall back to SurrealQL for non-Record types
                                        var modEntityId = GetEntityId(op.Entity);
                                        if (modEntityId is not null)
                                        {
                                            // Auto-generate Guid for Guid.Empty identity
                                            var modType = op.Entity.GetType();
                                            var modMp = Options.Schema.Mappings.GetValueOrDefault(modType);
                                            var modIdpName = modMp?.IdentityProperty ?? "Id";
                                            var modIdp = modType.GetProperty(modIdpName);
                                            if (modIdp is not null && modIdp.PropertyType == typeof(Guid) && modIdp.GetValue(op.Entity) is Guid modGuidVal && modGuidVal == Guid.Empty)
                                            {
                                                var newGuid = Guid.NewGuid();
                                                modIdp.SetValue(op.Entity, newGuid);
                                                modEntityId = newGuid.ToString();
                                            }

                                            if (TryBuildSurrealQlObjectLiteral(op.Entity, out var literal))
                                            {
                                                var response = await ExecuteRawWriteAsync(
                                                    targetSession,
                                                    $"UPSERT {table}:`{modEntityId}` MERGE {literal}",
                                                    null,
                                                    ct).ConfigureAwait(false);
                                                ThrowIfRawQueryFailed(response);
                                            }
                                            else
                                            {
                                                var response = await ExecuteRawWriteAsync(
                                                    targetSession,
                                                    $"UPSERT {table}:`{modEntityId}` MERGE $data",
                                                    new Dictionary<string, object?> { ["data"] = op.Entity },
                                                    ct).ConfigureAwait(false);
                                                ThrowIfRawQueryFailed(response);
                                            }
                                        }
                                    }
                                }
                                break;

                            case OperationType.Insert:
                                ResolvedLogger.LogDebug("INSERT {Type} ({Table})", op.EntityType.Name, table);
                                var insertId = GetEntityId(op.Entity);
                                if (!string.IsNullOrEmpty(insertId))
                                {
                                    // Auto-generate Guid for Guid.Empty identity
                                    var insType = op.Entity.GetType();
                                    var insMp = Options.Schema.Mappings.GetValueOrDefault(insType);
                                    var insIdpName = insMp?.IdentityProperty ?? "Id";
                                    var insIdp = insType.GetProperty(insIdpName);
                                    if (insIdp is not null && insIdp.PropertyType == typeof(Guid) && insIdp.GetValue(op.Entity) is Guid insGuidVal && insGuidVal == Guid.Empty)
                                    {
                                        var newGuid = Guid.NewGuid();
                                        insIdp.SetValue(op.Entity, newGuid);
                                        insertId = newGuid.ToString();
                                    }

                                    // Use CREATE (insert-only, fails if record already exists)
                                    if (TryBuildSurrealQlObjectLiteral(op.Entity, out var literal))
                                    {
                                        var response = await ExecuteRawWriteAsync(
                                            targetSession,
                                            $"CREATE {table}:`{insertId}` CONTENT {literal}",
                                            null,
                                            ct).ConfigureAwait(false);
                                        ThrowIfRawQueryFailed(response);
                                    }
                                    else
                                    {
                                        var response = await ExecuteRawWriteAsync(
                                            targetSession,
                                            $"CREATE {table}:`{insertId}` CONTENT $data",
                                            new Dictionary<string, object?> { ["data"] = op.Entity },
                                            ct).ConfigureAwait(false);
                                        ThrowIfRawQueryFailed(response);
                                    }
                                }
                                else
                                {
                                    // No explicit ID — auto-generate via CREATE
                                    var createdResult = await CreateEntityAsync(op, table, targetSession, ct).ConfigureAwait(false);
                                    if (createdResult is not null)
                                    {
                                        var idProp = op.EntityType.GetProperty("Id");
                                        var createdId = createdResult.GetType().GetProperty("Id")?.GetValue(createdResult);
                                        if (idProp is not null && createdId is not null)
                                            idProp.SetValue(op.Entity, createdId);
                                    }
                                }
                                break;

                            case OperationType.Update:
                                ResolvedLogger.LogDebug("UPDATE-ONLY {Type} ({Table})", op.EntityType.Name, table);
                                var updateId = GetEntityId(op.Entity);
                                if (!string.IsNullOrEmpty(updateId))
                                {
                                    // Auto-generate Guid for Guid.Empty identity
                                    var updType = op.Entity.GetType();
                                    var updMp = Options.Schema.Mappings.GetValueOrDefault(updType);
                                    var updIdpName = updMp?.IdentityProperty ?? "Id";
                                    var updIdp = updType.GetProperty(updIdpName);
                                    if (updIdp is not null && updIdp.PropertyType == typeof(Guid) && updIdp.GetValue(op.Entity) is Guid updGuidVal && updGuidVal == Guid.Empty)
                                    {
                                        var newGuid = Guid.NewGuid();
                                        updIdp.SetValue(op.Entity, newGuid);
                                        updateId = newGuid.ToString();
                                    }

                                    // Use UPDATE (update-only, fails if record doesn't exist)
                                    if (TryBuildSurrealQlObjectLiteral(op.Entity, out var literal))
                                    {
                                        var response = await ExecuteRawWriteAsync(
                                            targetSession,
                                            $"UPDATE {table}:`{updateId}` MERGE {literal}",
                                            null,
                                            ct).ConfigureAwait(false);
                                        ThrowIfRawQueryFailed(response);
                                    }
                                    else
                                    {
                                        var response = await ExecuteRawWriteAsync(
                                            targetSession,
                                            $"UPDATE {table}:`{updateId}` MERGE $data",
                                            new Dictionary<string, object?> { ["data"] = op.Entity },
                                            ct).ConfigureAwait(false);
                                        ThrowIfRawQueryFailed(response);
                                    }
                                }
                                break;

                            case OperationType.Deleted:
                                ResolvedLogger.LogDebug("DELETE {Type} ({Table})", op.EntityType.Name, table);
                                // Fast path: entity has a typed RecordId — preserve it
                                if (op.Entity is IRecord recDel && recDel.Id is not null)
                                {
                                    await targetSession.Delete(recDel.Id, ct).ConfigureAwait(false);
                                    break;
                                }
                                var delId = GetRecordId(op.Entity, table);
                                if (delId is not null)
                                    await targetSession.Delete(delId, ct).ConfigureAwait(false);
                                break;

                            case OperationType.SoftDeleted:
                                ResolvedLogger.LogDebug("SOFT-DELETE {Type} ({Table})", op.EntityType.Name, table);
                                if (op.Entity is ISoftDeleted sd)
                                {
                                    sd.Deleted = true;
                                    sd.DeletedAt = DateTimeOffset.UtcNow;
                                }
                                // Fast path: entity has a typed RecordId — preserve it
                                if (op.Entity is IRecord recSoft && recSoft.Id is not null)
                                {
                                    await UpsertRecordAsync(recSoft, recSoft.Id, targetSession, ct).ConfigureAwait(false);
                                    break;
                                }
                                var softDelId = GetRecordId(op.Entity, table);
                                if (softDelId is not null)
                                {
                                    if (op.Entity is IRecord record)
                                    {
                                        await UpsertRecordAsync(record, softDelId, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        var softDelEntityId = GetEntityId(op.Entity);
                                        if (softDelEntityId is not null)
                                        {
                                            var response = await ExecuteRawWriteAsync(
                                                targetSession,
                                                $"UPSERT {table}:`{softDelEntityId}` MERGE $data",
                                                new Dictionary<string, object?> { ["data"] = op.Entity },
                                                ct).ConfigureAwait(false);
                                            ThrowIfRawQueryFailed(response);
                                        }
                                    }
                                }
                                break;
                        }

                        // Call after-store/after-delete listeners
                        if (Options.Listeners.Count > 0)
                        {
                            foreach (var listener in Options.Listeners)
                            {
                                if (op.Type is OperationType.Added or OperationType.Modified)
                                    await listener.AfterStoreAsync(this, op.Entity, ct).ConfigureAwait(false);
                                else if (op.Type is OperationType.Deleted or OperationType.SoftDeleted)
                                    await listener.AfterDeleteAsync(this, op.Entity, ct).ConfigureAwait(false);
                            }
                        }
                        if (SessionListeners.Count > 0)
                        {
                            foreach (var listener in SessionListeners)
                            {
                                if (op.Type is OperationType.Added or OperationType.Modified)
                                    await listener.AfterStoreAsync(this, op.Entity, ct).ConfigureAwait(false);
                                else if (op.Type is OperationType.Deleted or OperationType.SoftDeleted)
                                    await listener.AfterDeleteAsync(this, op.Entity, ct).ConfigureAwait(false);
                            }
                        }
                    }

                    _unitOfWork.Clear();
                }

                // Phase 3.5: Flush FetchForWriting pending events before inline projections
                if (_fetchForWritingResults.Count > 0)
                {
                    foreach (var ffw in _fetchForWritingResults)
                    {
                        if (ffw.PendingEvents.Count > 0)
                        {
                            await Events.Append(ffw.StreamId, ffw.ExpectedVersion, ffw.PendingEvents, ct).ConfigureAwait(false);
                        }
                    }
                    _fetchForWritingResults.Clear();
                }

                // Phase 4: run inline projections on events appended during this session
                if (_appendedEvents.Count > 0 && Options.Projections.Count > 0)
                {
                    var inlineProjections = Options.Projections
                        .Where(p => p.Lifecycle == ProjectionLifecycle.Inline)
                        .ToList();

                    if (inlineProjections.Count > 0)
                    {
                        // Group appended events by stream (typed IEvent wrappers)
                        var streamGroups = _appendedEvents
                            .GroupBy(e => e.StreamId.ToString())
                            .ToDictionary(g => g.Key, g => g.ToList());

                        const int MaxReentrancyDepth = 10;
                        int depth = 0;
                        int totalEventsProcessedAtStart = _appendedEvents.Count;

                        do
                        {
                            bool hadSideEffects = false;

                            foreach (var projection in inlineProjections)
                            {
                                foreach (var (streamId, events) in streamGroups)
                                {
                                    var matchingEvents = events
                                        .Where(e => e.Data is not null && projection.EventTypes.Contains(e.Data.GetType()))
                                        .ToList();

                                    if (matchingEvents.Count == 0)
                                        continue;

                                    ResolvedLogger.LogInformation("Inline projection {ProjectionType} applied on stream {StreamId} (depth {Depth})",
                                        projection.GetType().Name, streamId, depth);

                                    // Enrichment hook: allow projections to pre-load reference data
                                    if (projection is IEnrichProjection enricher)
                                    {
                                        await enricher.EnrichAsync(this, matchingEvents.AsReadOnly(), ct).ConfigureAwait(false);
                                    }

                                    var context = new ProjectionContext(this, matchingEvents.AsReadOnly());
                                    await projection.ApplyAsync(context, ct).ConfigureAwait(false);

                                    // Collect side effects from this projection context
                                    if (context.SideEffects.Count > 0)
                                    {
                                        hadSideEffects = true;
                                        ResolvedLogger.LogDebug("Projection {ProjectionType} raised {Count} side effects",
                                            projection.GetType().Name, context.SideEffects.Count);

                                        foreach (var se in context.SideEffects)
                                        {
                                            if (se is AppendEventSideEffect append)
                                            {
                                                await Events.Append(append.StreamId, new[] { append.Event }, headers: null, ct).ConfigureAwait(false);
                                                ResolvedLogger.LogDebug("Side effect: appended {EventType} to stream {StreamId}",
                                                    append.Event.GetType().Name, append.StreamId);
                                            }
                                            // Future: PublishMessageSideEffect, etc.
                                        }
                                    }
                                }
                            }

                            // Phase 5 (inner): persist projected documents from this round
                            if (_unitOfWork.Operations.Count > 0)
                            {
                                foreach (var op in _unitOfWork.Operations)
                                {
                                    var table = MetadataDispatch.GetTableName(op.EntityType, Options.Schema);

                                    if (op.Type == OperationType.Deleted)
                                    {
                                        var delId = GetRecordId(op.Entity, table);
                                        if (delId is not null)
                                            await targetSession.Delete(delId, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        var entityId = GetEntityId(op.Entity);

                                            if (!string.IsNullOrEmpty(entityId) && op.Entity is IRecord record)
                                        {
                                            var rid = new RecordIdOf<string>(table, entityId);
                                            await UpsertRecordAsync(record, rid, targetSession, ct).ConfigureAwait(false);
                                        }
                                        else if (!string.IsNullOrEmpty(entityId))
                                        {
                                            // POCO projection with explicit identity — use MERGE to upsert
                                            var response = await ExecuteRawWriteAsync(
                                                targetSession,
                                                $"UPSERT {table}:`{entityId}` MERGE $data",
                                                new Dictionary<string, object?> { ["data"] = op.Entity },
                                                ct).ConfigureAwait(false);
                                            ThrowIfRawQueryFailed(response);
                                        }
                                        else
                                        {
                                            await targetSession.Create(table, op.Entity, ct).ConfigureAwait(false);
                                        }
                                    }
                                }

                                // Capture projection-generated operations for IChangeSet
                                committedOperations = committedOperations.Concat(_unitOfWork.Operations).ToArray();
                                _unitOfWork.Clear();
                            }

                            if (!hadSideEffects) break;

                            // Only include newly appended events from side effects (skip already-processed events)
                            var newEventCount = _appendedEvents.Count - totalEventsProcessedAtStart;
                            if (newEventCount <= 0) break;

                            streamGroups = _appendedEvents
                                .Skip(totalEventsProcessedAtStart)
                                .GroupBy(e => e.StreamId.ToString())
                                .ToDictionary(g => g.Key, g => g.ToList());
                            totalEventsProcessedAtStart = _appendedEvents.Count;

                            depth++;
                            if (depth >= MaxReentrancyDepth)
                            {
                                var latestStream = streamGroups.Keys.FirstOrDefault() ?? "unknown";
                                throw new ProjectionReentrancyException(latestStream, depth);
                            }
                        } while (true);

                        // Snapshot appended events for IChangeSet before clearing (preserve tuple format)
                        appendedEventSnapshot = _appendedEvents
                            .Select(e => ((string StreamId, object Event))(e.StreamId, e.Data))
                            .ToArray();
                        _appendedEvents.Clear();
                    }
                }

                // Phase 5: Execute queued patches (inside transaction, after entity operations and inline projections)
                if (_queuedPatches.Count > 0)
                {
                    foreach (var patch in _queuedPatches)
                    {
                        await patch.ExecuteAsync(this, ct).ConfigureAwait(false);
                    }
                    _queuedPatches.Clear();
                }

                // Phase 5b: Execute queued graph operations.
                // Explicit transactions stage graph mutations until CommitTransactionAsync so
                // rollback is deterministic even for embedded engines that persist writes eagerly.
                graphOpCount = _queuedRelations.Count + _queuedUnrelations.Count;
                if (_queuedRelations.Count > 0 || _queuedUnrelations.Count > 0)
                {
                    if (_explicitTransaction != null)
                    {
                        _transactionRelations.AddRange(_queuedRelations);
                        _transactionUnrelations.AddRange(_queuedUnrelations);
                        _queuedRelations.Clear();
                        _queuedUnrelations.Clear();
                    }
                    else
                    {
                        await ExecuteGraphOperationsAsync(targetSession, _queuedRelations, _queuedUnrelations, ct).ConfigureAwait(false);
                    }
                }

                // Phase 6: Execute queued storage operations (from QueueOperation)
                if (_queuedStorageOperations.Count > 0)
                {
                    foreach (var op in _queuedStorageOperations)
                    {
                        await op.ExecuteAsync(this, ct).ConfigureAwait(false);
                    }
                    _queuedStorageOperations.Clear();
                }

                // Phase 7: Execute queued SQL commands (from QueueSqlCommand)
                if (QueuedSqlCommands.Count > 0)
                {
                    await ExecuteQueuedSqlCommandsAsync(ct).ConfigureAwait(false);
                }

                // AfterSaveChangesAsync hooks (store + session level, inside transaction, before commit)
                if (Options.Listeners.Count > 0)
                {
                    foreach (var listener in Options.Listeners)
                        await listener.AfterSaveChangesAsync(this, ct).ConfigureAwait(false);
                }
                if (SessionListeners.Count > 0)
                {
                    foreach (var listener in SessionListeners)
                        await listener.AfterSaveChangesAsync(this, ct).ConfigureAwait(false);
                }

                // BeforeCommitAsync hooks (store + session level)
                if (Options.Listeners.Count > 0)
                {
                    foreach (var listener in Options.Listeners)
                        await listener.BeforeCommitAsync(this, ct).ConfigureAwait(false);
                }
                if (SessionListeners.Count > 0)
                {
                    foreach (var listener in SessionListeners)
                        await listener.BeforeCommitAsync(this, ct).ConfigureAwait(false);
                }

                if (ownsTx && tx is not null)
                {
                    await tx.Commit(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                if (ownsTx && tx is not null)
                {
                    await tx.Cancel(ct).ConfigureAwait(false);
                }
                throw;
            }

            // AfterCommitAsync hooks (called only after successful commit)
            var committedChanges = new ChangeSet
            {
                Operations = committedOperations,
                AppendedEvents = appendedEventSnapshot,
                Updated = committedOperations.Where(op => op.Type == OperationType.Modified).Select(op => op.Entity).ToArray(),
                Inserted = committedOperations.Where(op => op.Type == OperationType.Added).Select(op => op.Entity).ToArray(),
                Deleted = committedOperations.Where(op => op.Type is OperationType.Deleted or OperationType.SoftDeleted).Select(op => op.Entity).ToArray()
            };
            if (Options.Listeners.Count > 0)
            {
                foreach (var listener in Options.Listeners)
                    await listener.AfterCommitAsync(this, committedChanges, ct).ConfigureAwait(false);
            }
            if (SessionListeners.Count > 0)
            {
                foreach (var listener in SessionListeners)
                    await listener.AfterCommitAsync(this, committedChanges, ct).ConfigureAwait(false);
            }

            var resultCount = count > 0 ? count : appendedEventSnapshot.Length + graphOpCount;

            // Clear identity map and snapshots after successful save
            if (IsDirtyTracking)
            {
                foreach (var typeMap in IdentityMap.Values)
                    typeMap.Clear();
                ClearSnapshots();
            }

            ResolvedLogger.LogInformation("SaveChangesAsync: committed {Count} changes", resultCount);
            return resultCount;
        }
        catch (Exception ex) when (ex is not ConcurrencyException)
        {
            ResolvedLogger.LogError(ex, "SaveChangesAsync failed");
            throw new InvalidOperationException("Failed to save changes.", ex);
        }
        catch (ConcurrencyException)
        {
            // Let concurrency exceptions bubble up unwrapped so callers
            // can catch them directly.
            throw;
        }
    }

    private string? GetEntityId(object entity)
    {
        var entityType = entity.GetType();
        var meta = MetadataRegistry.TryGet(entityType);
        if (meta is not null && meta.GetRecordIdAccessor is not null)
            return meta.GetRecordIdAccessor(entity);

        // Look up identity property from document mapping configuration
        // This supports POCOs configured via Schema.For<T>().Identity(x => x.Id)
        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var idPropName = mapping?.IdentityProperty ?? "Id";

        // Fallback for non-generated types
        var prop = entityType.GetProperty(idPropName);
        if (prop is null) return null;

        var idValue = prop.GetValue(entity);
        if (idValue is null) return null;

        // RecordId does not override ToString(), so handle it explicitly
        if (idValue is RecordIdOf<string> strRid)
            return strRid.Id;
        if (idValue is RecordIdOf<long> longRid)
            return longRid.Id.ToString();
        if (idValue is RecordIdOf<int> intRid)
            return intRid.Id.ToString();

        var str = idValue.ToString();
        return string.IsNullOrEmpty(str) ? null : str;
    }

    private RecordIdOf<string>? GetRecordId(object entity, string table)
    {
        var id = GetEntityId(entity);
        return string.IsNullOrEmpty(id) ? null : new RecordIdOf<string>(table, id);
    }

    /// <summary>
    /// Sets the identity property on a POCO from a SurrealDB record ID string
    /// (e.g., <c>"table:42"</c> → sets <c>Id = 42</c> for <c>long</c> identity).
    /// For generated <c>Entity&lt;TId&gt;</c> types, the shim handles this — this method
    /// is a no-op for them. Supports <c>long</c>, <c>int</c>, <c>ulong</c>, <c>uint</c>,
    /// <c>string</c>, and <c>Guid</c> identity types.
    /// </summary>
    private void SetEntityIdFromRecordId(object entity, string? recordIdString)
    {
        if (string.IsNullOrEmpty(recordIdString)) return;

        var entityType = entity.GetType();
        var meta = MetadataRegistry.TryGet(entityType);
        // For generated types (Entity<TId>), the shim handles identity — skip
        if (meta is not null && meta.GetRecordIdAccessor is not null) return;

        // Look up the identity property from the document mapping
        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var idPropName = mapping?.IdentityProperty ?? "Id";
        var prop = entityType.GetProperty(idPropName);
        if (prop is null) return;

        // Parse: "table:id_value" → "id_value"
        var colonIndex = recordIdString.LastIndexOf(':');
        var idStr = colonIndex >= 0 ? recordIdString[(colonIndex + 1)..] : recordIdString;

        try
        {
            var propType = prop.PropertyType;
            object? convertedId = propType switch
            {
                _ when propType == typeof(long) => long.Parse(idStr),
                _ when propType == typeof(int)  => int.Parse(idStr),
                _ when propType == typeof(ulong) => ulong.Parse(idStr),
                _ when propType == typeof(uint)  => uint.Parse(idStr),
                _ when propType == typeof(string) => idStr,
                _ when propType == typeof(Guid)  => Guid.Parse(idStr),
                _ => Convert.ChangeType(idStr, propType)
            };

            prop.SetValue(entity, convertedId);
        }
        catch (FormatException fe)
        {
            throw new InvalidOperationException(
                $"Cannot convert record ID '{recordIdString}' to identity type '{prop.PropertyType.Name}'. " +
                $"The extracted id portion was not valid for this type.", fe);
        }
    }

    /// <summary>
    /// Fetches the current version of an entity from the database.
    /// Used by <see cref="UpdateExpectedVersion{T}"/> and <see cref="UpdateRevision{T}"/> checks.
    /// Returns -1 if no version field is found or the entity has no ID.
    /// </summary>
    private async Task<long> FetchVersionAsync(object entity, Type entityType, ISurrealDbSession session, CancellationToken ct)
    {
        if (MetadataDispatch.GetVersionFieldName(entityType) is null)
            return -1;

        var table = MetadataDispatch.GetTableName(entityType, Options.Schema);
        var id = GetEntityId(entity);
        if (id is null) return -1;

        var surql = $"SELECT * FROM {table}:`{id.Replace("`", "\\`")}`;";
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count == 0)
            return -1;

        var list = DeserializeResponseForEntityType(response, entityType);
        if (list.Count > 0 && list[0] is not null)
            return GetVersion(list[0]!);

        return -1;
    }

    /// <summary>
    /// Checks that the current database version of a Modified entity matches the
    /// version that was originally loaded/stored. Throws <see cref="ConcurrencyException"/>
    /// on mismatch.
    ///
    /// <para>
    /// <b>Note on atomicity:</b> The version check (SELECT) and the subsequent write (UPSERT)
    /// are performed within a SurrealDB transaction, so they are now atomic. Prior to the
    /// transactional wrapping, these were two separate operations with a small race window.
    /// </para>
    /// </summary>
    private async Task CheckConcurrencyAsync(Operation op, ISurrealDbSession session, CancellationToken ct)
    {
        var entity = op.Entity;
        var expectedVersion = GetTrackedVersion(entity);
        if (expectedVersion < 0) return;

        // Determine the version property name for this entity type
        if (MetadataDispatch.GetVersionFieldName(op.EntityType) is null) return;

        var table = MetadataDispatch.GetTableName(op.EntityType, Options.Schema);
        var id = GetEntityId(entity);
        if (id is null)
        {
            ResolvedLogger.LogWarning("Skipping concurrency check for {Type}: unable to resolve entity ID",
                op.EntityType.Name);
            return;
        }

        // Query the current version from the DB using a raw SurrealQL call
        // with typed GetValue<T> deserialization (same path as Query provider).
        var surql = $"SELECT * FROM {table}:`{id.Replace("`", "\\`")}`;";
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors)
        {
            ResolvedLogger.LogWarning("Skipping concurrency check for {Type}/{Id}: RawQuery returned errors",
                op.EntityType.Name, id);
            return;
        }

        long dbVersion = 0;
        if (response.Count > 0)
        {
            var list = DeserializeResponseForEntityType(response, op.EntityType);
            if (list.Count > 0)
            {
                var dbEntity = list[0];
                if (dbEntity is not null)
                {
                    dbVersion = GetVersion(dbEntity);
                }
            }
        }

        if (expectedVersion != dbVersion)
        {
            ResolvedLogger.LogWarning(
                "Concurrency conflict on {Type} (id={Id}): expected version {Expected}, found {Actual}",
                op.EntityType.Name, id, expectedVersion, dbVersion);

            throw new ConcurrencyException(op.EntityType, id, expectedVersion, dbVersion);
        }

        ResolvedLogger.LogDebug("Concurrency check passed for {Type} (id={Id}): version {Version}",
            op.EntityType.Name, id, dbVersion);
    }

    private System.Collections.IList DeserializeResponseForEntityType(SurrealDbResponse response, Type entityType)
    {
        var method = typeof(InternalSessionBase)
            .GetMethod(nameof(DeserializeMappedPocoResponse), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .MakeGenericMethod(entityType);
        return (System.Collections.IList)(method.Invoke(this, [response, 0]) ?? Array.Empty<object>());
    }

    /// <summary>
    /// Creates an entity in SurrealDB using <c>Session.Create&lt;T&gt;</c> with the
    /// correct runtime type, ensuring all properties (including version fields)
    /// are serialized by the CBOR serializer.
    /// </summary>
    private async Task<object?> CreateEntityAsync(Operation op, string table, ISurrealDbSession session, CancellationToken ct)
    {
        if (TryBuildSurrealQlObjectLiteral(op.Entity, out var literal))
        {
            var response = await ExecuteRawWriteAsync(
                session,
                $"CREATE {table} CONTENT {literal}",
                null,
                ct).ConfigureAwait(false);
            ThrowIfRawQueryFailed(response);
            return DeserializeCreatedEntity(response, op.EntityType);
        }

        try
        {
            // Use dynamic dispatch to invoke the correct generic Create<T> overload.
            // This avoids reflection issues with Task<T>.Result property.
            dynamic dynSession = session;
            dynamic dynEntity = op.Entity;
            return await dynSession.Create(table, dynEntity, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fallback to reflection-based Create for types not supported by dynamic dispatch
            if (CreateMethod is null)
                return await session.Create(table, op.Entity, ct).ConfigureAwait(false);

            var genericCreate = CreateMethod.MakeGenericMethod(op.EntityType);
            var rawResult = genericCreate.Invoke(session, [table, op.Entity, ct]);
            if (rawResult is not Task task) return null;

            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            return resultProp?.GetValue(task);
        }
    }

    private bool TryBuildSurrealQlObjectLiteral(object entity, out string literal)
    {
        literal = "";

        var properties = entity.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true }
                        && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
            .ToArray();

        var mapping = Options.Schema.Mappings.GetValueOrDefault(entity.GetType());
        var identityProperty = mapping?.IdentityProperty ?? "Id";

        var fields = new List<string>(properties.Length);

        foreach (var property in properties)
        {
            if (string.Equals(property.Name, identityProperty, StringComparison.OrdinalIgnoreCase))
                continue;

            var fieldName = MetadataDispatch.GetFieldName(entity.GetType(), property.Name, Options.Schema);
            fields.Add($"{fieldName}: {ToSurrealQlLiteral(property.GetValue(entity))}");
        }

        literal = "{ " + string.Join(", ", fields) + " }";
        return true;
    }

    private string ToSurrealQlLiteral(object? value)
    {
        return value switch
        {
            null => "NONE",
            string s => $"'{EscapeSurrealQlString(s)}'",
            char c => $"'{EscapeSurrealQlString(c.ToString())}'",
            bool b => b ? "true" : "false",
            GeometryPoint point => point.ToSurrealQL(),
            GeometryPolygon polygon => polygon.ToSurrealQL(),
            DateTime dt => $"d'{dt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'",
            DateTimeOffset dto => $"d'{dto.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'",
            Guid guid => $"'{guid}'",
            Enum e => Options.EnumStorage == EnumStorage.AsString
                ? $"'{e}'"
                : Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ when TryFormatExplicitRecordLinkLiteral(value, out var recordLink) => recordLink,
            System.Collections.IDictionary dictionary => ToSurrealQlDictionaryLiteral(dictionary),
            System.Collections.IEnumerable enumerable when value is not string => ToSurrealQlArrayLiteral(enumerable),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            _ => JsonElementToSurrealQL(JsonSerializer.SerializeToElement(
                value,
                Options.SerializerOptions ?? DefaultLiteralSerializerOptions))
        };
    }

    /// <summary>
    /// Recursively converts a <see cref="JsonElement"/> to a SurrealQL-compatible
    /// object literal. Used by the catch-all arm of <see cref="ToSurrealQlLiteral"/>
    /// to handle arbitrary complex POCOs and DTOs.
    /// </summary>
    private static string JsonElementToSurrealQL(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{ "
            + string.Join(", ",
                element.EnumerateObject()
                    .Select(p => $"{EscapeSurrealQlKey(p.Name)}: {JsonElementToSurrealQL(p.Value)}"))
            + " }",
        JsonValueKind.Array => "[ "
            + string.Join(", ", element.EnumerateArray().Select(JsonElementToSurrealQL))
            + " ]",
        JsonValueKind.String => $"'{EscapeSurrealQlString(element.GetString()!)}'",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "NONE",
        _ => "NONE"
    };

    private static string EscapeSurrealQlKey(string key)
    {
        // SurrealQL object keys don't need quoting if they're simple identifiers;
        // wrap in backticks or quotes when the key contains special characters.
        foreach (var ch in key)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') return $"`{key}`";
        }
        return key;
    }

    private static bool TryFormatExplicitRecordLinkLiteral(object value, out string literal)
        => TryFormatRecordIdObject(value, out literal);

    private static bool TryFormatRecordIdObject(object value, out string literal)
    {
        literal = "";

        if (TryFormatRecordIdLike(value, out literal))
            return true;

        if (value is RecordId recordId)
        {
            literal = FormatRecordIdLiteral(recordId);
            return true;
        }

        return false;
    }

    private string ToSurrealQlArrayLiteral(System.Collections.IEnumerable values)
    {
        var items = values.Cast<object?>().Select(v => ToSurrealQlLiteral(v));
        return "[" + string.Join(", ", items) + "]";
    }

    private string ToSurrealQlDictionaryLiteral(System.Collections.IDictionary dictionary)
    {
        var fields = dictionary.Keys
            .Cast<object?>()
            .Where(key => key is not null)
            .Select(key => $"{key}: {ToSurrealQlLiteral(dictionary[key!])}");

        return "{ " + string.Join(", ", fields) + " }";
    }

    private static string EscapeSurrealQlString(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private object? DeserializeCreatedEntity(SurrealDbResponse response, Type entityType)
    {
        var records = CborResultReader.ReadPocoResult(response, 0);
        if (records.Count == 0)
            return null;

        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var method = typeof(InternalSessionBase)
            .GetMethod(nameof(InternalSessionBase.DeserializePocoFromList), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .MakeGenericMethod(entityType);
        var result = method.Invoke(null, [records, mapping?.IdentityProperty ?? "Id", Options.Schema, Options.EnumStorage]);
        return result is System.Collections.IList { Count: > 0 } list ? list[0] : null;
    }

    private async Task<SurrealDbResponse> ExecuteRawWriteAsync(
        ISurrealDbSession session,
        string surql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (ResolvedLogger.IsEnabled(LogLevel.Debug))
        {
            ResolvedLogger.LogDebug(
                "SaveChangesAsync write SurrealQL: {SurrealQL}{Parameters}",
                surql,
                FormatWriteParameters(parameters));
        }

        var response = await session.RawQuery(surql, parameters, ct).ConfigureAwait(false);

        if (ResolvedLogger.IsEnabled(LogLevel.Debug))
        {
            ResolvedLogger.LogDebug(
                "SaveChangesAsync write response: HasErrors={HasErrors}; Count={Count}; FirstOk={FirstOk}; Errors={Errors}",
                response.HasErrors,
                response.Count,
                response.FirstOk is not null,
                FormatRawQueryErrors(response));
        }

        return response;
    }

    private static string FormatWriteParameters(IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return string.Empty;

        var entries = parameters.Select(parameter =>
        {
            var value = parameter.Value;
            return value is null
                ? $"{parameter.Key}=null"
                : $"{parameter.Key}=<{value.GetType().Name}>";
        });

        return "; Parameters: [" + string.Join(", ", entries) + "]";
    }

    private static string FormatRawQueryErrors(SurrealDbResponse response)
    {
        if (!response.HasErrors)
            return "";

        return string.Join("; ", response.Errors.Select(error =>
            error is SurrealDbErrorResult err ? err.Details : error.ToString()));
    }

    private static void ThrowIfRawQueryFailed(SurrealDbResponse response)
    {
        if (!response.HasErrors)
            return;

        var details = FormatRawQueryErrors(response);
        throw new InvalidOperationException(details);
    }

    /// <summary>
    /// Resolves the expected version field name for the given entity type.
    /// Delegates to <see cref="MetadataDispatch.GetVersionFieldName"/> which
    /// uses generated metadata when available, with reflection fallback.
    /// </summary>
    private static string? GetVersionFieldName(Type entityType)
        => MetadataDispatch.GetVersionFieldName(entityType);

    private async Task UpsertRecordAsync(IRecord record, RecordId rid, ISurrealDbSession session, CancellationToken ct)
    {
        if (TryBuildRelationshipRecordLiteral(record, out var literal)
            || TryBuildSurrealQlObjectLiteral(record, out literal))
        {
            var response = await ExecuteRawWriteAsync(
                session,
                $"UPSERT {FormatRecordIdLiteral(rid)} CONTENT {literal}",
                null,
                ct).ConfigureAwait(false);
            ThrowIfRawQueryFailed(response);
            return;
        }

        // Use the Upsert method via ISurrealDbSharedMethods interface.
        // We call the generic method with the record's runtime type.
        var entityType = record.GetType();
        var generic = UpsertMethod!.MakeGenericMethod(entityType, entityType);
        var task = (Task)generic.Invoke(session, [rid, record, ct])!;
        await task.ConfigureAwait(false);
    }

    private bool TryBuildRelationshipRecordLiteral(IRecord record, out string literal)
    {
        literal = "";
        var entityType = record.GetType();
        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var relationships = mapping?.GetRelationshipMappings();
        if (relationships is not { Count: > 0 })
            return false;

        var relationshipByMember = relationships
            .Where(r => r.ClrMemberName is not null && r.StorageKind == RelationshipStorageKind.RecordLink)
            .ToDictionary(r => r.ClrMemberName!, StringComparer.Ordinal);

        var fields = new List<string>();
        foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            if (property.Name == nameof(IRecord.Id))
                continue;

            if (relationshipByMember.TryGetValue(property.Name, out var relationship))
            {
                fields.Add($"{relationship.StorageFieldName}: {ToRelationshipLiteral(property.GetValue(record), relationship)}");
                continue;
            }

            var fieldName = MetadataDispatch.GetFieldName(entityType, property.Name, Options.Schema);
            fields.Add($"{fieldName}: {ToSurrealQlLiteral(property.GetValue(record))}");
        }

        literal = "{ " + string.Join(", ", fields) + " }";
        return true;
    }

    private static string ToRelationshipLiteral(object? value, RelationshipMapping relationship)
    {
        if (value is null)
            return "NONE";

        if (relationship.Kind == RelationshipKind.HasMany && value is System.Collections.IEnumerable values and not string)
            return "[" + string.Join(", ", values.Cast<object?>().Select(v => ToSingleRelationshipLiteral(v, relationship))) + "]";

        return ToSingleRelationshipLiteral(value, relationship);
    }

    private static string ToSingleRelationshipLiteral(object? value, RelationshipMapping relationship)
    {
        if (value is null)
            return "NONE";

        if (value is IRecord record && record.Id is not null)
            return FormatRecordIdLiteral(record.Id);

        if (value is RecordId recordId)
            return FormatRecordIdLiteral(recordId);

        return ToRecordIdLiteral(relationship.TargetTableName, value);
    }

    private static string ToRecordIdLiteral(string tableName, object id)
    {
        var value = id switch
        {
            string s => QuoteRecordIdValue(s),
            Guid g => QuoteRecordIdValue(g.ToString()),
            DateTime dt => QuoteRecordIdValue(dt.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset dto => QuoteRecordIdValue(dto.ToString("O", CultureInfo.InvariantCulture)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => QuoteRecordIdValue(id.ToString() ?? string.Empty)
        };

        return $"{tableName}:{value}";
    }

    private static string QuoteRecordIdValue(string value)
        => "`" + value.Replace("`", "\\`", StringComparison.Ordinal) + "`";

    private static string FormatRecordIdLiteral(RecordId recordId)
    {
        if (TryFormatRecordIdOf(recordId, out var literal))
            return literal;

        return recordId switch
        {
            RecordIdOf<string> s => $"{s.Table}:{QuoteRecordIdValue(s.Id)}",
            RecordIdOf<long> l => $"{l.Table}:{l.Id.ToString(CultureInfo.InvariantCulture)}",
            RecordIdOf<int> i => $"{i.Table}:{i.Id.ToString(CultureInfo.InvariantCulture)}",
            _ => $"{recordId.Table}:{QuoteRecordIdValue(recordId.DeserializeId<object>()?.ToString() ?? string.Empty)}"
        };
    }

    private static bool TryFormatRecordIdOf(object value, out string literal)
    {
        if (TryFormatRecordIdLike(value, out literal))
            return true;

        literal = "";
        var type = value.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(RecordIdOf<>))
            return false;

        var table = type.GetProperty("Table")?.GetValue(value)?.ToString();
        var id = type.GetProperty("Id")?.GetValue(value);
        if (string.IsNullOrWhiteSpace(table) || id is null)
            return false;

        literal = ToRecordIdLiteral(table, id);
        return true;
    }

    private static bool TryFormatRecordIdLike(object value, out string literal)
    {
        literal = "";
        var type = value.GetType();
        var table = type.GetProperty("Table", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)?.ToString();
        var id = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        if (string.IsNullOrWhiteSpace(table) || id is null)
            return false;

        literal = ToRecordIdLiteral(table, id);
        return true;
    }

    // ===================================================================
    // Marten API parity: Watch* live query wrappers
    // ===================================================================

    /// <summary>
    /// [Marten API parity] Starts a live query monitoring a table.
    /// Delegates to <see cref="ILiveQuerySession.Live{T}"/> internally,
    /// wrapping the result in <see cref="LegacyLiveQueryAdapter{T}"/>.
    /// </summary>
    public async Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class
    {
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.Live<T>().SubscribeAsync(ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<T>(sub);
    }

    /// <summary>
    /// [Marten API parity] Starts a live query with a raw SurrealQL WHERE clause.
    /// Delegates to <see cref="ILiveQuerySession.LiveRawQuery{T}"/> internally.
    /// </summary>
    public async Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"LIVE SELECT * FROM `{table}` WHERE {whereClause}";
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.LiveRawQuery<T>(surql, ct: ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<T>(sub);
    }

    /// <summary>
    /// [Marten API parity] Watches events for a specific stream ID (mt_events table).
    /// </summary>
    public async Task<ILiveQuery<object>> WatchStreamAsync(string streamId, CancellationToken ct = default)
    {
        var surql = $"LIVE SELECT * FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}'";
        var liveSession = new LiveQuerySession(
            Session, Options, Options.LoggerFactory, TenantId);
        var sub = await liveSession.LiveRawQuery<object>(surql, ct: ct).ConfigureAwait(false);
        return new LegacyLiveQueryAdapter<object>(sub);
    }

    /// <summary>Start a graph traversal query.</summary>
    public IGraphQuery<T> Graph<T>() where T : class
    {
        return GraphQueryProvider.Graph<T>(this);
    }

    /// <summary>Queue a graph edge for creation during <see cref="SaveChangesAsync"/>.</summary>
    public void Relate<TEdge>(
        RecordId from,
        RecordId to,
        TEdge? data = default) where TEdge : class
    {
        _queuedRelations.Add(new QueuedRelation(from, to, typeof(TEdge), data));
    }

    /// <summary>Queue a graph edge for deletion during <see cref="SaveChangesAsync"/>.</summary>
    public void Unrelate(RecordId edgeId)
    {
        _queuedUnrelations.Add(edgeId);
    }

    internal async Task FlushPendingGraphOperationsAsync(CancellationToken ct = default)
    {
        if (_queuedRelations.Count > 0)
        {
            _transactionRelations.AddRange(_queuedRelations);
            _queuedRelations.Clear();
        }

        if (_queuedUnrelations.Count > 0)
        {
            _transactionUnrelations.AddRange(_queuedUnrelations);
            _queuedUnrelations.Clear();
        }

        if (_transactionRelations.Count == 0 && _transactionUnrelations.Count == 0)
            return;

        var schemaName = ResolveGraphSchemaName(_transactionRelations);
        var targetSession = await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);

        await ExecuteGraphOperationsAsync(
            targetSession,
            _transactionRelations,
            _transactionUnrelations,
            ct).ConfigureAwait(false);
    }

    private string? ResolveGraphSchemaName(IReadOnlyCollection<QueuedRelation> relations)
    {
        if (relations.Count == 0)
            return null;

        var targets = relations
            .Select(r => MetadataDispatch.GetSchemaTarget(r.EdgeType, Options.Schema).Database)
            .Distinct()
            .ToList();

        if (targets.Count > 1)
        {
            var dbNames = string.Join(", ", targets.Select(d => $"'{d ?? "test"}'"));
            throw new InvalidOperationException(
                $"Cross-database graph operations are not supported. Queued relations span multiple databases: {dbNames}");
        }

        return targets[0];
    }

    private static async Task ExecuteGraphOperationsAsync(
        ISurrealDbSession session,
        List<QueuedRelation> relations,
        List<RecordId> unrelations,
        CancellationToken ct)
    {
        foreach (var rel in relations)
        {
            var table = MetadataDispatch.GetTableName(rel.EdgeType);
            var genericRelate = RelateMethod!.MakeGenericMethod(rel.EdgeType, rel.EdgeType);
            var task = (Task)genericRelate.Invoke(session, [table, rel.From, rel.To, rel.Data, ct])!;
            await task.ConfigureAwait(false);
        }
        relations.Clear();

        foreach (var edgeId in unrelations)
        {
            var ridStr = edgeId switch
            {
                RecordIdOf<string> s => $"{s.Table}:{s.Id}",
                RecordIdOf<long> l => $"{l.Table}:{l.Id}",
                RecordIdOf<int> i => $"{i.Table}:{i.Id}",
                _ => throw new ArgumentException(
                    $"Unsupported RecordId type '{edgeId.GetType().Name}'. Expected RecordIdOf<string>, RecordIdOf<long>, or RecordIdOf<int>.",
                    nameof(edgeId))
            };
            await session.RawQuery($"DELETE {ridStr};", null, ct).ConfigureAwait(false);
        }
        unrelations.Clear();
    }

    // ===================================================================
    // IDocumentSession additions (Marten API parity)
    // ===================================================================

    /// <inheritdoc />
    public IDocumentSession ForTenant(string tenantId)
    {
        SetTenant(tenantId);
        return this;
    }

    /// <summary>
    /// Session-scoped listeners (in addition to store-level listeners).
    /// Delegates to the <see cref="InternalSessionBase.SessionListeners"/> list
    /// populated by the <see cref="DocumentSession"/> constructor.
    /// </summary>
    public IList<IDocumentSessionListener> Listeners => SessionListeners;

    /// <inheritdoc />
    public ConcurrencyChecks? Concurrency
    {
        get => _concurrencyOverride;
        set => _concurrencyOverride = value;
    }

    /// <inheritdoc />
    public async Task<IDocumentSession> IdentitySessionForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (DocumentStore is null)
            throw new InvalidOperationException("DocumentStore is not available on this session.");
        return await DocumentStore.IdentitySessionAsync(tenantId, ct).ConfigureAwait(false);
    }

    // ===================================================================
    // IQuerySession additions (Marten API parity)
    // ===================================================================

    /// <inheritdoc />
    public async Task<T?> LoadAsync<T>(int id, CancellationToken ct = default) where T : class
    {
        if (typeof(ISableDocument<int>).IsAssignableFrom(typeof(T)))
            return await base.LoadAsync<T>(id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        RequestCount++;
        var table = MetadataDispatch.GetTableName(typeof(T));
        var rid = new RecordIdOf<int>(table, id);
        var strId = id.ToString(CultureInfo.InvariantCulture);

        if (ShouldTrackInIdentityMap(typeof(T))
            && IdentityMap.TryGetValue(typeof(T), out var typeMap)
            && typeMap.TryGetValue(strId, out var cached))
            return (T?)cached;

        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var loadSession = await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);
        return await loadSession.Select<T>(rid, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> LoadAsync<T>(long id, CancellationToken ct = default) where T : class
    {
        if (typeof(ISableDocument<long>).IsAssignableFrom(typeof(T)))
            return await base.LoadAsync<T>(id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        RequestCount++;
        var table = MetadataDispatch.GetTableName(typeof(T));
        var rid = new RecordIdOf<long>(table, id);
        var strId = id.ToString(CultureInfo.InvariantCulture);

        if (ShouldTrackInIdentityMap(typeof(T))
            && IdentityMap.TryGetValue(typeof(T), out var typeMap)
            && typeMap.TryGetValue(strId, out var cached))
            return (T?)cached;

        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var loadSession = await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);
        return await loadSession.Select<T>(rid, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => LoadAsync<T>(id.ToString(), ct);

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        var strId = id.ToString();
        return base.LoadAsync<T>(strId!, ct);
    }

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(string id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(int id, CancellationToken ct = default) where T : class
        => CheckExistsAsync<T>(id.ToString(), ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(long id, CancellationToken ct = default) where T : class
        => CheckExistsAsync<T>(id.ToString(), ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => CheckExistsAsync<T>(id.ToString(), ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        var strId = id.ToString();
        return CheckExistsAsyncCore<T>(strId!, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyAsync<T>(this, ids, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<Guid> ids, CancellationToken ct = default) where T : class
        => LoadManyAsync<T>(ids.Select(id => id.ToString()), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<long> ids, CancellationToken ct = default) where T : class
        => LoadManyAsync<T>(ids.Select(id => id.ToString()), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<int> ids, CancellationToken ct = default) where T : class
        => LoadManyAsync<T>(ids.Select(id => id.ToString()), ct);

    /// <inheritdoc />
    public async Task<IDocumentMetadata?> MetadataForAsync<T>(T entity, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is IDocumentMetadata existing)
            return existing;

        var id = GetEntityId(entity);
        if (id is null) return null;

        var fresh = await LoadAsync<T>(id, ct).ConfigureAwait(false);
        return fresh as IDocumentMetadata;
    }

    // ===================================================================
    // IDocumentOperations additions (Marten API parity)
    // ===================================================================

    /// <inheritdoc />
    public void Delete<T>(string id) where T : class
    {
        var entity = CreateEntityWithId<T>(id);
        Delete(entity);
    }

    /// <inheritdoc />
    public void Delete<T>(long id) where T : class
        => Delete<T>(id.ToString());

    /// <inheritdoc />
    public void Delete<T>(int id) where T : class
        => Delete<T>(id.ToString());

    /// <inheritdoc />
    public void Delete<T>(Guid id) where T : class
        => Delete<T>(id.ToString());

    /// <inheritdoc />
    public void Delete<T>(object id) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        Delete<T>(id.ToString()!);
    }

    /// <inheritdoc />
    public void Store<T>(IEnumerable<T> documents) where T : class
    {
        foreach (var doc in documents)
            Store(doc);
    }

    /// <inheritdoc />
    public void Store<T>(params T[] documents) where T : class
    {
        foreach (var doc in documents)
            Store(doc);
    }

    private static readonly ConcurrentDictionary<Type, Action<DocumentSession, object>> _storeCache = new();
    private static readonly ConcurrentDictionary<Type, Action<DocumentSession, object>> _deleteCache = new();

    /// <inheritdoc />
    public void StoreObjects(IEnumerable<object> documents)
    {
        foreach (var doc in documents)
        {
            if (doc is null) continue;
            var type = doc.GetType();
            var action = _storeCache.GetOrAdd(type, t =>
            {
                var method = typeof(DocumentSession).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .First(m => m.Name == nameof(Store)
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == m.GetGenericArguments()[0]);
                var generic = method.MakeGenericMethod(t);
                return (session, obj) => generic.Invoke(session, [obj]);
            });
            action(this, doc);
        }
    }

    /// <inheritdoc />
    public void DeleteObjects(IEnumerable<object> documents)
    {
        foreach (var doc in documents)
        {
            if (doc is null) continue;
            var type = doc.GetType();
            var action = _deleteCache.GetOrAdd(type, t =>
            {
                var method = typeof(DocumentSession).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .First(m => m.Name == nameof(Delete)
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == m.GetGenericArguments()[0]);
                var generic = method.MakeGenericMethod(t);
                return (session, obj) => generic.Invoke(session, [obj]);
            });
            action(this, doc);
        }
    }

    /// <inheritdoc />
    public void Insert<T>(IEnumerable<T> documents) where T : class
    {
        foreach (var doc in documents)
            _unitOfWork.Add(doc, OperationType.Insert);
    }

    /// <inheritdoc />
    public void Insert<T>(params T[] documents) where T : class
    {
        foreach (var doc in documents)
            _unitOfWork.Add(doc, OperationType.Insert);
    }

    private static readonly ConcurrentDictionary<Type, Action<DocumentSession, object>> _insertCache = new();

    /// <inheritdoc />
    public void InsertObjects(IEnumerable<object> documents)
    {
        foreach (var doc in documents)
        {
            if (doc is null) continue;
            var type = doc.GetType();
            var action = _insertCache.GetOrAdd(type, t =>
            {
                var method = typeof(DocumentSession).GetMethod(nameof(Insert), 1, [t])!;
                var generic = method.MakeGenericMethod(t);
                return (session, obj) => generic.Invoke(session, [obj]);
            });
            action(this, doc);
        }
    }

    private readonly HashSet<Type> _identityMapForTypes = new();

    /// <inheritdoc />
    public void UseIdentityMapFor<T>() where T : class
    {
        _identityMapForTypes.Add(typeof(T));
    }

    /// <summary>
    /// Override: also track in identity map for types registered via <see cref="UseIdentityMapFor{T}"/>.
    /// </summary>
    internal protected override bool ShouldTrackInIdentityMap(Type type)
        => base.ShouldTrackInIdentityMap(type) || _identityMapForTypes.Contains(type);

    private readonly List<IStorageOperation> _queuedStorageOperations = new();

    /// <inheritdoc />
    public void QueueOperation(IStorageOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _queuedStorageOperations.Add(operation);
    }

    /// <summary>
    /// Tracks expected versions for entities registered via <see cref="UpdateExpectedVersion{T}"/>.
    /// Key is the entity instance (reference equality), value is the expected version.
    /// </summary>
    private readonly Dictionary<object, long> _expectedVersions = new();
    /// <summary>
    /// Tracks expected revisions for entities registered via <see cref="UpdateRevision{T}"/>.
    /// Key is the entity instance (reference equality), value is the expected revision.
    /// </summary>
    private readonly Dictionary<object, int> _expectedRevisions = new();
    /// <summary>
    /// Tracks entities registered via <see cref="TryUpdateRevision{T}"/> where revision mismatch should be silently skipped.
    /// </summary>
    private readonly HashSet<object> _tryUpdateRevisions = new();

    /// <inheritdoc />
    public void UpdateExpectedVersion<T>(T entity, long expectedVersion) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        Store(entity);
        _expectedVersions[entity] = expectedVersion;
    }

    /// <inheritdoc />
    public void UpdateRevision<T>(T entity, int revision) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        Store(entity);
        _expectedRevisions[entity] = revision;
    }

    /// <inheritdoc />
    public void TryUpdateRevision<T>(T entity, int revision) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        Store(entity);
        _expectedRevisions[entity] = revision;
        _tryUpdateRevisions.Add(entity);
    }

    /// <inheritdoc />
    public void Update<T>(IEnumerable<T> documents) where T : class
    {
        foreach (var doc in documents)
            _unitOfWork.Add(doc, OperationType.Update);
    }

    /// <inheritdoc />
    public void Update<T>(params T[] documents) where T : class
    {
        foreach (var doc in documents)
            _unitOfWork.Add(doc, OperationType.Update);
    }

    /// <inheritdoc />
    public void HardDelete<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequestCount++;
        // Always use OperationType.Deleted (hard delete), even for ISoftDeleted types
        _unitOfWork.Add(entity, OperationType.Deleted);
        ResolvedLogger.LogDebug("Queued {Type} for hard-deletion", typeof(T).Name);
    }

    /// <inheritdoc />
    public void HardDelete<T>(string id) where T : class
    {
        var entity = CreateEntityWithId<T>(id);
        HardDelete(entity);
    }

    /// <inheritdoc />
    public void HardDelete<T>(long id) where T : class
        => HardDelete<T>(id.ToString());

    /// <inheritdoc />
    public void HardDelete<T>(int id) where T : class
        => HardDelete<T>(id.ToString());

    /// <inheritdoc />
    public void HardDelete<T>(Guid id) where T : class
        => HardDelete<T>(id.ToString());

    /// <inheritdoc />
    public async Task<long> HardDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var whereClause = BuildWhereClause(predicate);
        var sql = $"DELETE FROM {table} WHERE {whereClause};";
        RequestCount++;
        var response = await Session.RawQuery(sql, null, ct).ConfigureAwait(false);
        return response.Count;
    }

    /// <inheritdoc />
    public async Task<long> UndoDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var whereClause = BuildWhereClause(predicate);
        var sql = $"UPDATE {table} SET deleted = false WHERE deleted = true AND {whereClause};";
        RequestCount++;
        var response = await Session.RawQuery(sql, null, ct).ConfigureAwait(false);
        return response.Count;
    }

    /// <inheritdoc />
    public IUnitOfWork PendingChanges => (IUnitOfWork)_unitOfWork;

    /// <inheritdoc />
    public void EjectById<T>(T document) where T : class
    {
        var id = GetEntityId(document);
        if (id is not null)
            Eject<T>(id);
    }

    /// <inheritdoc />
    public void Eject<T>(T entity) where T : class
    {
        var id = GetEntityId(entity);
        if (id is not null)
            Eject<T>(id);
        _unitOfWork.Eject(entity);
    }

    private static readonly ConcurrentDictionary<Type, Action<DocumentSession>> _ejectAllDelegates = new();

    /// <inheritdoc />
    public void EjectAllOfType(Type type)
    {
        var action = _ejectAllDelegates.GetOrAdd(type, t =>
        {
            var method = typeof(InternalSessionBase).GetMethod("EjectAll", 1, Type.EmptyTypes)!
                .MakeGenericMethod(t);
            var param = System.Linq.Expressions.Expression.Parameter(typeof(DocumentSession), "s");
            var call = System.Linq.Expressions.Expression.Call(param, method);
            return System.Linq.Expressions.Expression.Lambda<Action<DocumentSession>>(call, param).Compile();
        });
        action(this);
        _unitOfWork.EjectAllOfType(type);
    }

    /// <summary>
    /// Creates a minimal entity of type T with the given string ID set on its Id property.
    /// Uses <see cref="Activator.CreateInstance{T}"/> which requires a parameterless constructor.
    /// </summary>
    private T CreateEntityWithId<T>(string id) where T : class
    {
        var entity = Activator.CreateInstance<T>();
        var mapping = Options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var idPropName = mapping?.IdentityProperty ?? "Id";
        var idProp = typeof(T).GetProperty(idPropName, BindingFlags.Public | BindingFlags.Instance);
        if (idProp is not null && idProp.CanWrite)
        {
            var propType = idProp.PropertyType;
            var actualType = Nullable.GetUnderlyingType(propType) ?? propType;
            object convertedId = id;
            if (actualType == typeof(long))
                convertedId = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(int))
                convertedId = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(ulong))
                convertedId = ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(uint))
                convertedId = uint.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(byte))
                convertedId = byte.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(short))
                convertedId = short.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
            else if (actualType == typeof(Guid))
                convertedId = Guid.Parse(id);
            else if (actualType == typeof(DateTime))
                convertedId = DateTime.Parse(
                    id,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
            else if (actualType == typeof(RecordId))
                convertedId = RecordId.From(MetadataDispatch.GetTableName(typeof(T)), id);
            else if (actualType.IsGenericType && actualType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
                convertedId = Activator.CreateInstance(propType, id)!;
            idProp.SetValue(entity, convertedId);
        }
        return entity;
    }

    /// <summary>
    /// Disposes the session. If an explicit transaction is active and owned by this session,
    /// cancels it before disposing the underlying SurrealDB session.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (_explicitTransaction is not null && _ownsTransaction)
        {
            await _explicitTransaction.Cancel(DefaultCt).ConfigureAwait(false);
            await _explicitTransaction.DisposeAsync().ConfigureAwait(false);
            _explicitTransaction = null;
            _ownsTransaction = false;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps EventStore to track appended events for inline projections.
    /// </summary>
    private sealed class TrackingEventStore : IEvents
    {
        private readonly IEvents _inner;
        private readonly DocumentSession _owner;

        public TrackingEventStore(IEvents inner, DocumentSession owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public async Task<IReadOnlyList<IEvent>> Append(string streamId, IEnumerable<object> events, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var result = await _inner.Append(streamId, events, headers, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.Append(streamId, expectedVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendOptimistic(streamId, lastKnownVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendExclusive(streamId, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendOptimistic(streamId, lastKnownVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendExclusive(streamId, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            // Marten's StartStream is commonly called without awaiting before SaveChangesAsync.
            // Complete the append here so that compatibility pattern still feeds inline projections.
            var result = _inner.Append(streamId, events, headers: null, ct).GetAwaiter().GetResult();
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return Task.FromResult(streamId);
        }

        public async Task<FetchForWritingResult<T>> FetchForWritingAsync<T>(string streamId, CancellationToken ct = default) where T : class
        {
            var result = await _inner.FetchForWritingAsync<T>(streamId, ct).ConfigureAwait(false);
            _owner._fetchForWritingResults.Add(result);
            return result;
        }

        public async Task WriteToAggregate<T>(
            Guid streamId,
            int version,
            Action<FetchForWritingResult<T>> handler,
            CancellationToken ct = default)
            where T : class
        {
            var stream = await FetchForWritingAsync<T>(streamId.ToString(), ct).ConfigureAwait(false);
            handler(stream);
            await _owner.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public Task<T?> AggregateStreamAsync<T>(
            string streamId,
            long? version = null,
            DateTimeOffset? timestamp = null,
            T? state = default,
            long? fromVersion = null,
            CancellationToken ct = default) where T : class
            => _inner.AggregateStreamAsync(streamId, version, timestamp, state, fromVersion, ct);

        public Task<StreamState?> FetchStreamStateAsync(string streamId, CancellationToken ct = default)
            => _inner.FetchStreamStateAsync(streamId, ct);

        public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken ct = default)
            => _inner.FetchStreamStateAsync(streamId, ct);

        public Task<IReadOnlyList<IEvent>> FetchStreamAsync(
            string streamId,
            long? version = null,
            DateTimeOffset? timestamp = null,
            long? fromVersion = null,
            CancellationToken ct = default)
            => _inner.FetchStreamAsync(streamId, version, timestamp, fromVersion, ct);

        public Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default)
            => StartStream(streamId, events, ct);

        public Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
            => StartStream(streamId.ToString("D"), events, ct);

        public Task<IReadOnlyList<IEvent>> FetchStream(string streamId, CancellationToken ct = default)
            => _inner.FetchStream(streamId, ct);

        public Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
            long sequence, CancellationToken ct = default)
            => _inner.FetchAllAfterSequence(sequence, ct);

        public Task ArchiveStream(string streamId, CancellationToken ct = default)
            => _inner.ArchiveStream(streamId, ct);

        public Task ArchiveStream(Guid streamId, CancellationToken ct = default)
            => _inner.ArchiveStream(streamId, ct);

        public async Task<IReadOnlyList<IEvent>> WriteTombstone(string streamId, long version, CancellationToken ct = default)
        {
            var result = await _inner.WriteTombstone(streamId, version, ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<int> BulkInsertEventsAsync(
            IEnumerable<(string StreamId, IEnumerable<object> Events)> streams,
            int batchSize = 100,
            CancellationToken ct = default)
        {
            // Bulk insert doesn't add to _appendedEvents since it bypasses per-stream tracking
            return await _inner.BulkInsertEventsAsync(streams, batchSize, ct).ConfigureAwait(false);
        }

        public Task<T?> AggregateStreamToLastKnownAsync<T>(string streamId, CancellationToken ct = default) where T : class
            => _inner.AggregateStreamToLastKnownAsync<T>(streamId, ct);

        public Task CompactStreamAsync<T>(string streamId, Action<CompactStreamOptions>? configure = null, CancellationToken ct = default) where T : class
            => _inner.CompactStreamAsync<T>(streamId, configure, ct);

        public Task<FetchForWritingResult<T>?> FetchForExclusiveWriting<T>(string streamId, CancellationToken ct = default) where T : class
            => _inner.FetchForExclusiveWriting<T>(streamId, ct);

        public ISurrealDbQueryable<T> QueryRawEventDataOnly<T>() where T : class
            => _inner.QueryRawEventDataOnly<T>();

        public ISurrealDbQueryable<IEvent> QueryAllRawEvents()
            => _inner.QueryAllRawEvents();

        public IEvent BuildEvent(object data)
            => _inner.BuildEvent(data);

        public Task OverwriteEventAsync(IEvent e, CancellationToken ct = default)
            => _inner.OverwriteEventAsync(e, ct);

        public Task DeleteSingleEventAsync(string streamId, long eventSequence, CancellationToken ct = default)
            => _inner.DeleteSingleEventAsync(streamId, eventSequence, ct);

        public Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken ct = default)
            => _inner.EventsExistAsync(query, ct);
    }
}

internal readonly record struct QueuedRelation(
    RecordId From,
    RecordId To,
    Type EdgeType,
    object? Data);
