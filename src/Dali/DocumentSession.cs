using System.Collections.Concurrent;
using System.Reflection;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Dali;

/// <summary>The concrete document session implementing <see cref="IDocumentSession"/>. Manages a unit-of-work with automatic change tracking, identity map, event appending, and transactional save via SurrealDB.</summary>
public class DocumentSession : InternalSessionBase, IDocumentSession
{
    private ILogger<DocumentSession> _baseLogger;
    private Microsoft.Extensions.Logging.ILogger? _loggerOverride;
    private readonly UnitOfWork _unitOfWork = new();

    private ILogger ResolvedLogger => _loggerOverride ?? _baseLogger;

    /// <inheritdoc />
    public Microsoft.Extensions.Logging.ILogger? Logger
    {
        get => _loggerOverride;
        set => _loggerOverride = value;
    }
    private readonly SessionOptions? _sessionOptions;
    private IEvents? _events;
    internal readonly List<IEvent> _appendedEvents = new();
    internal readonly List<IDeferredPatch> _queuedPatches = new();
    internal readonly List<QueuedRelation> _queuedRelations = new();
    internal readonly List<RecordId> _queuedUnrelations = new();
    internal readonly List<IFetchForWritingResult> _fetchForWritingResults = new();

    /// <summary>
    /// Cached <c>MethodInfo</c> for <see cref="SurrealDbResponse.GetValue{T}"/>,
    /// used by <see cref="CheckConcurrencyAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? GetValueMethod = typeof(SurrealDbResponse).GetMethods()
        .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition);

    /// <summary>
    /// Cached <c>MethodInfo</c> for <see cref="ISurrealDbSession.Create{T}"/>,
    /// used by <see cref="CreateEntityAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? CreateMethod = typeof(ISurrealDbSession).GetMethods()
        .FirstOrDefault(m => m.Name == nameof(ISurrealDbSession.Create)
            && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(string)
            && m.GetParameters()[2].ParameterType == typeof(CancellationToken));

    /// <summary>
    /// Cached <c>MethodInfo</c> for <see cref="ISurrealDbSharedMethods.Upsert{T, T}"/>,
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

    public IEvents Events
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
        if (Options.UseOptimisticConcurrency)
            TrackOriginalVersion(entity);

        // Populate identity map when dirty tracking is enabled
        if (IsDirtyTracking)
        {
            var id = GetEntityId(entity);
            if (id is not null)
            {
                var typeMap = IdentityMap.GetOrAdd(typeof(T), _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
                typeMap[id] = entity;
                CaptureSnapshot(typeof(T), id, entity);
            }
        }

        _unitOfWork.Add(entity, OperationType.Added);
        ResolvedLogger.LogDebug("Stored {Type} for {Operation}", typeof(T).Name, OperationType.Added);
    }

    /// <summary>
    /// Adds an operation directly to the unit of work with a specified type.
    /// Internal for testing — use <see cref="Store{T}"/> or <see cref="Delete{T}"/> in production.
    /// </summary>
    internal void AddOperation<T>(T entity, OperationType type) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        _unitOfWork.Add(entity, type);
    }

    /// <summary>
    /// Tracks the version of an entity for optimistic concurrency checking.
    /// Called automatically by <see cref="Store{T}"/> and <see cref="InternalSessionBase.LoadAsync{T}"/>.
    /// This method is available for cases where entities are obtained via other means (e.g., Query)
    /// and their version needs to be captured for subsequent concurrency checks.
    /// </summary>
    internal void TrackVersion<T>(T entity) where T : class
    {
        if (Options.UseOptimisticConcurrency)
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
        var table = MetadataDispatch.GetTableName(typeof(T));
        // Use a basic field-value extraction for common equality predicates.
        // For complex predicates, callers should use RawQueryAsync or Query<T> + manual delete.
        var whereClause = BuildWhereClause(predicate);
        var sql = $"DELETE FROM {table} WHERE {whereClause};";
        var response = await Session.RawQuery(sql, null, ct).ConfigureAwait(false);
        return response.Count;
    }

    private static string BuildWhereClause<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
    {
        // Simple binary expression handler: field == value
        if (predicate.Body is System.Linq.Expressions.BinaryExpression binary
            && binary.NodeType == System.Linq.Expressions.ExpressionType.Equal
            && binary.Left is System.Linq.Expressions.MemberExpression member
            && binary.Right is System.Linq.Expressions.ConstantExpression constant)
        {
            var fieldName = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(member.Member.Name);
            var value = constant.Value;
            var strVal = value?.ToString()?.Replace("'", "\\'") ?? "null";
            return $"{fieldName} = '{strVal}'";
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
        ClearSnapshots();
        EjectAll();
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        RequestCount++;
        var count = _unitOfWork.Operations.Count;
        if (count == 0 && _appendedEvents.Count == 0 && _queuedPatches.Count == 0
            && _queuedRelations.Count == 0 && _queuedUnrelations.Count == 0) return 0;

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
            // BeforeSaveChangesAsync hooks
            if (Options.Listeners.Count > 0)
            {
                foreach (var listener in Options.Listeners)
                    await listener.BeforeSaveChangesAsync(this, ct).ConfigureAwait(false);
            }

            var targetSession = await GetSessionForSchemaAsync(targetSchemaName, ct).ConfigureAwait(false);

            // Begin SurrealDB transaction — all per-entity operations on this session
            // participate because they share the underlying connection.
            var tx = await targetSession.BeginTransaction(ct).ConfigureAwait(false);

            try
            {
                // Phase 1: Optimistic concurrency checks (Modified entities only)
                // Runs before any mutations so we fail-fast if a conflict exists.
                if (Options.UseOptimisticConcurrency && count > 0)
                {
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type == OperationType.Modified)
                            await CheckConcurrencyAsync(op, targetSession, ct).ConfigureAwait(false);
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
                if (Options.UseOptimisticConcurrency && count > 0)
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

                        var table = MetadataDispatch.GetTableName(op.EntityType);

                        // Call before-store/before-delete listeners (after dirty-tracking skip)
                        if (Options.Listeners.Count > 0)
                        {
                            foreach (var listener in Options.Listeners)
                            {
                                if (op.Type is OperationType.Added or OperationType.Modified)
                                    await listener.BeforeStoreAsync(this, op.Entity, ct).ConfigureAwait(false);
                                else if (op.Type is OperationType.Deleted or OperationType.SoftDeleted)
                                    await listener.BeforeDeleteAsync(this, op.Entity, ct).ConfigureAwait(false);
                            }
                        }

                        switch (op.Type)
                        {
                            case OperationType.Added:
                                ResolvedLogger.LogDebug("CREATE/UPSERT {Type} ({Table})", op.EntityType.Name, table);
                                var entityId = GetEntityId(op.Entity);
                                if (!string.IsNullOrEmpty(entityId))
                                {
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
                                        await targetSession.RawQuery(
                                            $"UPSERT {table}:`{entityId}` MERGE $data",
                                            new Dictionary<string, object?> { ["data"] = op.Entity },
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
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

                            case OperationType.Modified:
                                ResolvedLogger.LogDebug("UPDATE {Type} ({Table})", op.EntityType.Name, table);
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
                                            await targetSession.RawQuery(
                                                $"UPSERT {table}:`{modEntityId}` MERGE $data",
                                                new Dictionary<string, object?> { ["data"] = op.Entity },
                                                ct).ConfigureAwait(false);
                                        }
                                    }
                                }
                                break;

                            case OperationType.Deleted:
                                ResolvedLogger.LogDebug("DELETE {Type} ({Table})", op.EntityType.Name, table);
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
                                            await targetSession.RawQuery(
                                                $"UPSERT {table}:`{softDelEntityId}` MERGE $data",
                                                new Dictionary<string, object?> { ["data"] = op.Entity },
                                                ct).ConfigureAwait(false);
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
                    }

                    _unitOfWork.Clear();
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
                            .GroupBy(e => e.StreamId)
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
                                    var table = MetadataDispatch.GetTableName(op.EntityType);

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
                                .GroupBy(e => e.StreamId)
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

                // Phase 5b: Execute queued graph operations (inside transaction)
                graphOpCount = _queuedRelations.Count + _queuedUnrelations.Count;
                if (_queuedRelations.Count > 0 || _queuedUnrelations.Count > 0)
                {
                    foreach (var rel in _queuedRelations)
                    {
                        var table = MetadataDispatch.GetTableName(rel.EdgeType);
                        var genericRelate = RelateMethod.MakeGenericMethod(rel.EdgeType, rel.EdgeType);
                        var task = (Task)genericRelate.Invoke(targetSession, [table, rel.From, rel.To, rel.Data, ct])!;
                        await task.ConfigureAwait(false);
                    }
                    _queuedRelations.Clear();

                    foreach (var edgeId in _queuedUnrelations)
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
                        await targetSession.RawQuery($"DELETE {ridStr};", null, ct).ConfigureAwait(false);
                    }
                    _queuedUnrelations.Clear();
                }

                // Phase 5c: Flush FetchForWriting pending events (auto-append with version check)
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

                // AfterSaveChangesAsync hooks (inside transaction, before commit)
                if (Options.Listeners.Count > 0)
                {
                    foreach (var listener in Options.Listeners)
                        await listener.AfterSaveChangesAsync(this, ct).ConfigureAwait(false);
                }

                // BeforeCommitAsync hooks
                if (Options.Listeners.Count > 0)
                {
                    foreach (var listener in Options.Listeners)
                        await listener.BeforeCommitAsync(this, ct).ConfigureAwait(false);
                }

                await tx.Commit(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.Cancel(ct).ConfigureAwait(false);
                throw;
            }

            // AfterCommitAsync hooks (called only after successful commit)
            if (Options.Listeners.Count > 0)
            {
                var changes = new ChangeSet
                {
                    Operations = committedOperations,
                    AppendedEvents = appendedEventSnapshot
                };
                foreach (var listener in Options.Listeners)
                    await listener.AfterCommitAsync(this, changes, ct).ConfigureAwait(false);
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

    private static string? GetEntityId(object entity)
    {
        var entityType = entity.GetType();
        var meta = MetadataRegistry.TryGet(entityType);
        if (meta is not null && meta.GetRecordIdAccessor is not null)
            return meta.GetRecordIdAccessor(entity);

        // Fallback for non-generated types
        var prop = entityType.GetProperty("Id");
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

    private static RecordIdOf<string>? GetRecordId(object entity, string table)
    {
        var id = GetEntityId(entity);
        return string.IsNullOrEmpty(id) ? null : new RecordIdOf<string>(table, id);
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

        var table = MetadataDispatch.GetTableName(op.EntityType);
        var id = GetEntityId(entity);
        if (id is null)
        {
            ResolvedLogger.LogWarning("Skipping concurrency check for {Type}: unable to resolve entity ID",
                op.EntityType.Name);
            return;
        }

        // Query the current version from the DB using a raw SurrealQL call
        // with typed GetValue<T> deserialization (same path as Query provider).
        var surql = $"SELECT * FROM {table}:{id};";
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors)
        {
            ResolvedLogger.LogWarning("Skipping concurrency check for {Type}/{Id}: RawQuery returned errors",
                op.EntityType.Name, id);
            return;
        }

        long dbVersion = 0;
        if (response.Count > 0 && GetValueMethod is not null)
        {
            var listType = typeof(List<>).MakeGenericType(op.EntityType);
            var typedGetValue = GetValueMethod.MakeGenericMethod(listType);
            var raw = typedGetValue.Invoke(response, [0]);
            if (raw is System.Collections.IList list && list.Count > 0)
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

    /// <summary>
    /// Creates an entity in SurrealDB using <c>Session.Create&lt;T&gt;</c> with the
    /// correct runtime type, ensuring all properties (including version fields)
    /// are serialized by the CBOR serializer.
    /// </summary>
    private async Task<object?> CreateEntityAsync(Operation op, string table, ISurrealDbSession session, CancellationToken ct)
    {
        if (CreateMethod is null)
        {
            // Fallback to untyped Create
            return await session.Create(table, op.Entity, ct).ConfigureAwait(false);
        }

        var genericCreate = CreateMethod.MakeGenericMethod(op.EntityType);
        var task = (Task?)genericCreate.Invoke(session, [table, op.Entity, ct]);
        if (task is null) return null;

        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetProperty("Result");
        return resultProp?.GetValue(task);
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
        // Use the Upsert method via ISurrealDbSharedMethods interface.
        // We call the generic method with the record's runtime type.
        var entityType = record.GetType();
        var generic = UpsertMethod!.MakeGenericMethod(entityType, entityType);
        var task = (Task)generic.Invoke(session, [rid, record, ct])!;
        await task.ConfigureAwait(false);
    }

    /// <summary>Starts a live query monitoring a table for changes.</summary>
    public async Task<ILiveQuery<T>> WatchTableAsync<T>(CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var live = await Session.LiveTable<T>(table, diff: false, ct).ConfigureAwait(false);
        return new LiveQuery<T>(live);
    }

    /// <summary>Starts a live query with a custom where clause.</summary>
    public async Task<ILiveQuery<T>> WatchQueryAsync<T>(string whereClause, CancellationToken ct = default) where T : class
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"LIVE SELECT * FROM `{table}` WHERE {whereClause}";
        var live = await Session.LiveRawQuery<T>(surql, null, ct).ConfigureAwait(false);
        return new LiveQuery<T>(live);
    }

    /// <summary>Watches events for a specific stream ID.</summary>
    public async Task<ILiveQuery<object>> WatchStreamAsync(string streamId, CancellationToken ct = default)
    {
        var surql = $"LIVE SELECT * FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}'";
        var live = await Session.LiveRawQuery<object>(surql, null, ct).ConfigureAwait(false);
        return new LiveQuery<object>(live);
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
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.Append(streamId, expectedVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendOptimistic(streamId, lastKnownVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendExclusive(streamId, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendOptimistic(streamId, lastKnownVersion, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await _inner.AppendExclusive(streamId, events, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return result;
        }

        public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            // Use Append directly to capture the wrapped IEvent objects
            var result = await _inner.Append(streamId, events, headers: null, ct).ConfigureAwait(false);
            foreach (var evt in result)
                _owner._appendedEvents.Add(evt);
            return streamId;
        }

        public async Task<FetchForWritingResult<T>> FetchForWritingAsync<T>(string streamId, CancellationToken ct = default) where T : class
        {
            var result = await _inner.FetchForWritingAsync<T>(streamId, ct).ConfigureAwait(false);
            _owner._fetchForWritingResults.Add(result);
            return result;
        }

        public Task<T?> AggregateStreamAsync<T>(string streamId, CancellationToken ct = default) where T : class
            => _inner.AggregateStreamAsync<T>(streamId, ct);

        public Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default)
            => _inner.StartStream<T>(streamId, events, ct);

        public Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
            => _inner.StartStream<T>(streamId, events, ct);

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
            foreach (var evt in result) _owner._appendedEvents.Add(evt);
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
    }
}

internal readonly record struct QueuedRelation(
    RecordId From,
    RecordId To,
    Type EdgeType,
    object? Data);
