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
    private const int MaximumInlineLiteralDepth = 8;

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
    private IAsyncDisposable? _explicitTransactionLease;
    private SurrealDbTransaction? _pendingAutoEventTransaction;
    private IAsyncDisposable? _pendingAutoEventTransactionLease;
    private ISurrealDbSession? _activeSaveSession;
    private readonly List<IChangeSet> _pendingExplicitChangeSets = [];
    private readonly List<DocumentVersionFence> _queuedVersionFences = [];
    private readonly List<DocumentVersionFence> _pendingExplicitVersionFences = [];
    private bool _explicitTransactionFailed;
    private bool _isSavingChanges;

    internal protected override ISurrealDbSession OperationSession =>
        _activeSaveSession ?? _explicitTransaction ?? _pendingAutoEventTransaction ?? Session;

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

    internal protected override bool UseOptimisticConcurrencyFor(Type documentType)
    {
        if (_concurrencyOverride.HasValue)
            return _concurrencyOverride.Value == ConcurrencyChecks.Enabled;

        return Options.UseOptimisticConcurrency
            || Options.Schema.Mappings.GetValueOrDefault(documentType)?.UseOptimisticConcurrency == true;
    }

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
        if (_pendingAutoEventTransaction != null)
            throw new InvalidOperationException("Pending event changes must be saved or cleared before beginning an explicit transaction.");

        var coordinated = EmbeddedTransactionCoordinator
            .BeginAsync(Session, DefaultCt)
            .GetAwaiter()
            .GetResult();
        _explicitTransaction = coordinated.Transaction;
        _explicitTransactionLease = coordinated.Lease;
        _ownsTransaction = true;
        _explicitTransactionFailed = false;
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
        if (_pendingAutoEventTransaction != null)
            throw new InvalidOperationException("Pending event changes must be saved or cleared before beginning an explicit transaction.");

        var coordinated = await EmbeddedTransactionCoordinator.BeginAsync(Session, ct).ConfigureAwait(false);
        _explicitTransaction = coordinated.Transaction;
        _explicitTransactionLease = coordinated.Lease;
        _ownsTransaction = true;
        _explicitTransactionFailed = false;
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
        if (_explicitTransactionFailed)
        {
            throw new InvalidOperationException(
                "The active transaction failed a version fence or save operation and cannot be committed. " +
                "Roll it back before continuing.");
        }

        var transaction = _explicitTransaction;
        var committed = false;
        IChangeSet[] committedChangeSets = [];
        try
        {
            await FlushPendingGraphOperationsAsync(ct).ConfigureAwait(false);
            await transaction.Commit(ct).ConfigureAwait(false);
            MarkVersionFencesCommitted(_pendingExplicitVersionFences);
            committedChangeSets = _pendingExplicitChangeSets.ToArray();
            _pendingExplicitChangeSets.Clear();
            _pendingExplicitVersionFences.Clear();
            committed = true;
        }
        catch
        {
            MarkVersionFencesFailed(_pendingExplicitVersionFences);
            _pendingExplicitChangeSets.Clear();
            _pendingExplicitVersionFences.Clear();
            _queuedRelations.Clear();
            _queuedUnrelations.Clear();
            _transactionRelations.Clear();
            _transactionUnrelations.Clear();
            throw;
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await ReleaseExplicitTransactionLeaseAsync().ConfigureAwait(false);
                }
                finally
                {
                    _explicitTransaction = null;
                    _ownsTransaction = false;
                    _explicitTransactionFailed = false;
                }
            }
        }
        if (committed)
        {
            foreach (var changes in committedChangeSets)
                await InvokeAfterCommitListenersAsync(changes, ct).ConfigureAwait(false);
        }
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
        _pendingExplicitChangeSets.Clear();
        _appendedEvents.Clear();
        _fetchForWritingResults.Clear();
        _unitOfWork.StreamIds.Clear();
        var rolledBack = false;
        try
        {
            await transaction.Cancel(CancellationToken.None).ConfigureAwait(false);
            rolledBack = true;
        }
        finally
        {
            if (rolledBack)
            {
                MarkVersionFencesRolledBack(_queuedVersionFences);
                MarkVersionFencesRolledBack(_pendingExplicitVersionFences);
            }
            else
            {
                MarkVersionFencesFailed(_queuedVersionFences);
                MarkVersionFencesFailed(_pendingExplicitVersionFences);
            }
            _queuedVersionFences.Clear();
            _pendingExplicitVersionFences.Clear();
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await ReleaseExplicitTransactionLeaseAsync().ConfigureAwait(false);
                }
                finally
                {
                    _explicitTransaction = null;
                    _ownsTransaction = false;
                    _explicitTransactionFailed = false;
                }
            }
        }
        _pendingExplicitChangeSets.Clear();
    }

    private async Task ReleaseExplicitTransactionLeaseAsync()
    {
        var lease = _explicitTransactionLease;
        try
        {
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_explicitTransactionLease, lease))
                _explicitTransactionLease = null;
        }
    }

    public override IEvents Events
    {
        get
        {
            if (_events is null)
            {
                _events = new TrackingEventStore(this);
            }
            return _events;
        }
    }

    private async Task<ISurrealDbSession> GetEventMutationSessionAsync(CancellationToken ct)
    {
        if (_activeSaveSession is not null)
            return _activeSaveSession;
        if (_explicitTransaction is not null)
            return _explicitTransaction;
        if (_pendingAutoEventTransaction is not null)
            return _pendingAutoEventTransaction;

        var coordinated = await EmbeddedTransactionCoordinator.BeginAsync(Session, ct).ConfigureAwait(false);
        _pendingAutoEventTransaction = coordinated.Transaction;
        _pendingAutoEventTransactionLease = coordinated.Lease;
        return _pendingAutoEventTransaction;
    }

    private async Task DiscardPendingAutoEventChangesAsync()
    {
        var transaction = _pendingAutoEventTransaction;
        var lease = _pendingAutoEventTransactionLease;
        try
        {
            if (transaction is not null)
                await transaction.Cancel(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (transaction is not null)
                    await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (lease is not null)
                        await lease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (ReferenceEquals(_pendingAutoEventTransaction, transaction))
                    {
                        _pendingAutoEventTransaction = null;
                        _pendingAutoEventTransactionLease = null;
                    }

                    _appendedEvents.Clear();
                    _fetchForWritingResults.Clear();
                    _unitOfWork.StreamIds.Clear();
                }
            }
        }
    }

    private Task DiscardFailedDirectEventMutationAsync()
        => _activeSaveSession is null && _explicitTransaction is null
            ? DiscardPendingAutoEventChangesAsync()
            : Task.CompletedTask;

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
        => QueueStoreOperation(entity, OperationType.Added);

    private void QueueStoreOperation<T>(T entity, OperationType operationType)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RequestCount++;

        // Store is an upsert operation. When the entity was materialized by this
        // session, preserve that provenance and queue an update so optimistic
        // concurrency compares the captured database version. A newly-created
        // entity remains an add, including repeated Store calls before the first
        // save.
        var alreadyQueued = _unitOfWork.Operations.Any(
            operation => ReferenceEquals(operation.Entity, entity));
        if (operationType == OperationType.Added
            && !alreadyQueued
            && HasTrackedOriginalVersion(entity))
        {
            operationType = OperationType.Modified;
        }

        // If conjoined tenancy is active and the entity has a TenantId property, set it
        // DatabasePerTenant isolates at the database level — no entity-level tenant ID needed.
        if (!string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
        {
            var meta = MetadataRegistry.TryGet<T>();
            if (meta is not null)
            {
                var entityTenant = meta.GetTenantId(entity);
                if (!string.IsNullOrWhiteSpace(entityTenant)
                    && !string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
                {
                    throw new SableEncryptionException(
                        $"Document tenant '{entityTenant}' does not match session tenant '{TenantId}'.");
                }
                meta.SetTenantId(entity, TenantId);
            }
            else
            {
                var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
                if (tenantProp is not null && tenantProp.CanWrite)
                {
                    var entityTenant = tenantProp.GetValue(entity) as string;
                    if (!string.IsNullOrWhiteSpace(entityTenant)
                        && !string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
                    {
                        throw new SableEncryptionException(
                            $"Document tenant '{entityTenant}' does not match session tenant '{TenantId}'.");
                    }
                    tenantProp.SetValue(entity, TenantId);
                }
            }
        }

        // Track original version for optimistic concurrency
        if (UseOptimisticConcurrencyFor(typeof(T)))
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

        _unitOfWork.Add(entity, operationType);
        ResolvedLogger.LogDebug("Stored {Type} for {Operation}", typeof(T).Name, operationType);
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
        if (UseOptimisticConcurrencyFor(typeof(T)))
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
            var propType = Nullable.GetUnderlyingType(idProp.PropertyType) ?? idProp.PropertyType;
            var normalizedId = DocumentIdentityResolver.NormalizeForDocumentType(
                typeof(T),
                id,
                Options.Schema);
            object convertedId = normalizedId;
            if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
            {
                var valueType = propType.GetGenericArguments()[0];
                var value = valueType.IsInstanceOfType(normalizedId)
                    ? normalizedId
                    : Convert.ChangeType(normalizedId, valueType, CultureInfo.InvariantCulture);
                convertedId = Activator.CreateInstance(
                    propType,
                    MetadataDispatch.GetTableName(typeof(T), Options.Schema),
                    value)!;
            }
            else if (!propType.IsInstanceOfType(convertedId))
            {
                convertedId = propType == typeof(Guid)
                    ? Guid.Parse(id)
                    : Convert.ChangeType(normalizedId, propType, CultureInfo.InvariantCulture);
            }
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
        EncryptedQueryGuard.Validate(predicate, Options.Schema);
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var (whereClause, parameters) = BuildMutationWhereClause(predicate);
        var sql = $"DELETE FROM {table} WHERE {whereClause};";
        var targetSession = await GetWriteSessionForSchemaAsync(mappedDatabase, ct).ConfigureAwait(false);
        var response = await EmbeddedTransactionRawQuery
            .ExecuteAsync(targetSession, sql, parameters, ct)
            .ConfigureAwait(false);
        response.EnsureAllOks();
        return response.Count;
    }

    private (string WhereClause, IReadOnlyDictionary<string, object?> Parameters)
        BuildMutationWhereClause<T>(
            System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        SurrealQueryResult translated;
        try
        {
            var query = Query<T>().Where(predicate);
            translated = new SurrealExpressionVisitor(Options.Schema, Options.EnumStorage)
                .Translate(query.Expression);
        }
        catch (Exception exception) when (exception is not SableEncryptionException)
        {
            throw new NotSupportedException(
                $"The mutation predicate for '{typeof(T).FullName}' could not be translated safely. " +
                "No records were changed.",
                exception);
        }

        if (translated.Where.Count == 0)
        {
            throw new NotSupportedException(
                $"The mutation predicate for '{typeof(T).FullName}' produced no WHERE clause. " +
                "No records were changed.");
        }

        var clauses = translated.Where.ToList();
        var parameters = translated.Parameters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        AddTenantMutationFilter(typeof(T), clauses, parameters);
        return (string.Join(" AND ", clauses), parameters);
    }

    private void AddTenantMutationFilter(
        Type entityType,
        ICollection<string> clauses,
        IDictionary<string, object?> parameters)
    {
        if (!TryGetTenantMutationField(entityType, out var tenantField))
            return;

        const string parameterName = "__sable_mutation_tenant";
        parameters[parameterName] = TenantId;
        clauses.Add($"{tenantField} = ${parameterName}");
    }

    private bool TryGetTenantMutationField(Type entityType, out string tenantField)
    {
        tenantField = "";
        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
            return false;

        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        if (mapping?.IsMultiTenanted == true && string.IsNullOrWhiteSpace(TenantId))
        {
            throw new InvalidOperationException(
                $"A tenant-scoped session is required to mutate multi-tenanted document " +
                $"type '{entityType.FullName}'.");
        }

        var hasTenantId = MetadataDispatch.HasTenantId(entityType);
        if (mapping?.IsMultiTenanted == true && !hasTenantId)
        {
            throw new InvalidOperationException(
                $"Multi-tenanted document type '{entityType.FullName}' must expose a writable " +
                "string TenantId property before it can be mutated.");
        }

        if (string.IsNullOrWhiteSpace(TenantId) || !hasTenantId)
            return false;

        tenantField = MetadataDispatch.GetFieldName(entityType, "TenantId", Options.Schema);
        return true;
    }

    private void ValidateHardDeleteTenant(object entity)
    {
        var entityType = entity.GetType();
        if (!TryGetTenantMutationField(entityType, out _))
            return;

        var entityTenant = entityType
            .GetProperty("TenantId", typeof(string))
            ?.GetValue(entity) as string;
        if (!string.IsNullOrWhiteSpace(entityTenant)
            && !string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot hard-delete entity belonging to tenant '{entityTenant}' " +
                $"from a session scoped to tenant '{TenantId}'.");
        }
    }

    private async Task DeleteRecordAsync(
        Type entityType,
        RecordId recordId,
        ISurrealDbSession targetSession,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantMutationField(entityType, out var tenantField))
        {
            await targetSession.Delete(recordId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__sable_delete_record"] = recordId,
            ["__sable_delete_tenant"] = TenantId
        };
        var response = await EmbeddedTransactionRawQuery.ExecuteAsync(
                targetSession,
                $"DELETE $__sable_delete_record " +
                $"WHERE {tenantField} = $__sable_delete_tenant;",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureAllOks();
    }

    public Task<int> BulkInsertAsync<T>(IEnumerable<T> documents, int batchSize = 100, CancellationToken ct = default) where T : class
    {
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), Options.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "bulk insert");
        var list = documents as IReadOnlyList<T> ?? documents.ToList();
        return BulkOperations.BulkInsertAsync(this, list, batchSize, ct);
    }

    public void ClearChanges()
    {
        if (_explicitTransactionFailed)
        {
            throw new InvalidOperationException(
                "The active transaction failed and must be rolled back before pending changes can be cleared.");
        }

        if (_pendingAutoEventTransaction is not null)
            DiscardPendingAutoEventChangesAsync().GetAwaiter().GetResult();

        _unitOfWork.Clear();
        _appendedEvents.Clear();
        _queuedPatches.Clear();
        _queuedRelations.Clear();
        _queuedUnrelations.Clear();
        _transactionRelations.Clear();
        _transactionUnrelations.Clear();
        _queuedStorageOperations.Clear();
        QueuedSqlCommands.Clear();
        MarkVersionFencesRolledBack(_queuedVersionFences);
        _queuedVersionFences.Clear();
        _expectedVersions.Clear();
        _expectedRevisions.Clear();
        _tryUpdateRevisions.Clear();
        ClearSnapshots();
        EjectAll();
    }

    public async Task<int> SaveChangesAsync(CancellationToken token = default)
    {
        if (_explicitTransactionFailed)
        {
            throw new InvalidOperationException(
                "The active transaction failed a version fence or save operation and cannot accept more changes. " +
                "Roll it back before continuing.");
        }

        var ct = token;
        RequestCount++;
        var count = _unitOfWork.Operations.Count;
        if (count == 0 && _appendedEvents.Count == 0 && _queuedPatches.Count == 0
            && _queuedRelations.Count == 0 && _queuedUnrelations.Count == 0
            && _fetchForWritingResults.Count == 0
            && _queuedStorageOperations.Count == 0 && QueuedSqlCommands.Count == 0
            && _queuedVersionFences.Count == 0
            && _pendingAutoEventTransaction is null) return 0;

        ResolvedLogger.LogInformation("SaveChangesAsync: committing {EntityCount} entities and {EventCount} events",
            count, _appendedEvents.Count);

        // Snapshots for IChangeSet in AfterCommitAsync
        var committedOperations = _unitOfWork.Operations.ToArray();
        var versionFenceSnapshot = _queuedVersionFences.ToArray();
        (string StreamId, object Event)[] appendedEventSnapshot = [];
        int graphOpCount = 0;

        // Cross-DB check: group operations by their database target.
        // SurrealDB cannot span multiple databases in a single transaction,
        // so we reject cross-database batches up front.
        // This check must happen BEFORE the try/catch so the exception is not wrapped.
        ValidateVersionFenceOperationOverlap(versionFenceSnapshot);
        ValidateVersionFenceSessionScope(versionFenceSnapshot);

        var databaseTargets = _unitOfWork.Operations
            .Select(op => MetadataDispatch.GetSchemaTarget(op.EntityType, Options.Schema).Database)
            .Concat(versionFenceSnapshot.Select(fence => fence.Database))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (databaseTargets.Count > 1)
        {
            var dbNames = string.Join(", ",
                databaseTargets.Select(database => $"'{database ?? Options.Database ?? "test"}'"));
            throw new InvalidOperationException(
                $"Cross-database transactions are not supported. " +
                $"Unit of work spans multiple databases: {dbNames}");
        }

        // Resolve the target session for this database (null = default database)
        var targetSchemaName = databaseTargets.Count > 0 ? databaseTargets[0] : null;

        // BeginTransaction[Async] is created from the session's default database.
        // Never replace a schema-routed session with that transaction: doing so
        // would execute the unit of work against the wrong physical database.
        ValidateExplicitTransactionDatabaseTarget(targetSchemaName);

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

        _isSavingChanges = true;
        try
        {
            var targetSession = await GetWriteSessionForSchemaAsync(targetSchemaName, ct).ConfigureAwait(false);

            // Begin SurrealDB transaction — all per-entity operations on this session
            // participate because they share the underlying connection.
            SurrealDbTransaction? tx = null;
            IAsyncDisposable? transactionLease = null;
            bool ownsTx = false;
            bool commitAttempted = false;

            if (_explicitTransaction != null)
            {
                // Use the explicit transaction — caller manages commit/rollback.
                // Operations run inside the explicit transaction scope.
                // Do NOT commit/cancel at the end — caller will do it.
                targetSession = _explicitTransaction;
            }
            else if (_pendingAutoEventTransaction != null)
            {
                if (targetSchemaName is not null)
                {
                    await DiscardPendingAutoEventChangesAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "Pending event changes cannot be combined with documents targeting a different database session.");
                }

                tx = _pendingAutoEventTransaction;
                transactionLease = _pendingAutoEventTransactionLease;
                ownsTx = true;
                if (tx is not null)
                    targetSession = tx;
            }
            else
            {
                var coordinated = await EmbeddedTransactionCoordinator.BeginAsync(targetSession, ct).ConfigureAwait(false);
                tx = coordinated.Transaction;
                transactionLease = coordinated.Lease;
                ownsTx = true;

                if (tx is not null)
                    targetSession = tx;
            }

            _activeSaveSession = targetSession;

            try
            {
                if (versionFenceSnapshot.Length > 0
                    && tx is null
                    && _explicitTransaction is null)
                {
                    throw new InvalidOperationException(
                        "Document version fences require an active SurrealDB transaction.");
                }

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

                // Phase 0.5: Apply mutating version fences before any ordinary document
                // writes. A later failure rolls the increments back with the transaction.
                if (versionFenceSnapshot.Length > 0)
                {
                    ValidateVersionFenceOperationOverlap(versionFenceSnapshot);
                    ValidateVersionFenceSessionScope(versionFenceSnapshot);
                    await ApplyVersionFencesAsync(versionFenceSnapshot, targetSession, ct).ConfigureAwait(false);
                }

                // Phase 1: Optimistic concurrency checks (Modified entities only)
                // Runs before any mutations so we fail-fast if a conflict exists.
                HashSet<object>? revisionSkipOps = null;
                if ((_unitOfWork.Operations.Any(op => UseOptimisticConcurrencyFor(op.EntityType))
                        || _expectedVersions.Count > 0
                        || _expectedRevisions.Count > 0)
                    && count > 0)
                {
                    revisionSkipOps = new HashSet<object>();
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type is OperationType.Modified or OperationType.Update)
                        {
                            // Standard optimistic concurrency check
                            if (UseOptimisticConcurrencyFor(op.EntityType))
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
                        if (op.Type is OperationType.Modified or OperationType.Update)
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
                if (count > 0)
                {
                    foreach (var op in _unitOfWork.Operations)
                    {
                        if (op.Type is OperationType.Added or OperationType.Modified
                            && UseOptimisticConcurrencyFor(op.EntityType)
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

                        var table = MetadataDispatch.GetTableName(op.EntityType, Options.Schema);

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
                                GenerateGuidIdentityIfEmpty(op.Entity);
                                if (DocumentIdentityResolver.TryResolve(
                                        op.Entity,
                                        table,
                                        Options.Schema,
                                        out var addedIdentity))
                                {
                                    // Use Upsert (create-or-update) for entities with explicit IDs.
                                    // This avoids failure when the record already exists (e.g. from
                                    // inline projections run in a prior session, or RebuildAsync).
                                    if (op.Entity is IRecord rec)
                                    {
                                        await UpsertRecordAsync(rec, addedIdentity.RecordId, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // Non-IRecord (Entity<TId>) types with explicit IDs.
                                        await ExecutePocoWriteAsync(
                                            targetSession,
                                            $"UPSERT {addedIdentity.Literal} CONTENT",
                                            op.Entity,
                                            ct).ConfigureAwait(false);
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
                                GenerateGuidIdentityIfEmpty(op.Entity);
                                if (DocumentIdentityResolver.TryResolve(
                                        op.Entity,
                                        table,
                                        Options.Schema,
                                        out var modifiedIdentity))
                                {
                                    if (op.Entity is IRecord record)
                                    {
                                        // Use Upsert (create-or-update) via the SDK's typed path,
                                        // which avoids CBOR serialization issues with JsonElement values.
                                        await UpsertRecordAsync(record, modifiedIdentity.RecordId, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // Fall back to SurrealQL for non-Record types
                                        await ExecutePocoWriteAsync(
                                            targetSession,
                                            $"UPSERT {modifiedIdentity.Literal} MERGE",
                                            op.Entity,
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                break;

                            case OperationType.Insert:
                                ResolvedLogger.LogDebug("INSERT {Type} ({Table})", op.EntityType.Name, table);
                                GenerateGuidIdentityIfEmpty(op.Entity);
                                if (DocumentIdentityResolver.TryResolve(
                                        op.Entity,
                                        table,
                                        Options.Schema,
                                        out var insertIdentity))
                                {
                                    // Use CREATE (insert-only, fails if record already exists)
                                    await ExecutePocoWriteAsync(
                                        targetSession,
                                        $"CREATE {insertIdentity.Literal} CONTENT",
                                        op.Entity,
                                        ct).ConfigureAwait(false);
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
                                GenerateGuidIdentityIfEmpty(op.Entity);
                                if (DocumentIdentityResolver.TryResolve(
                                        op.Entity,
                                        table,
                                        Options.Schema,
                                        out var updateIdentity))
                                {
                                    // Use UPDATE (update-only, fails if record doesn't exist)
                                    await ExecutePocoWriteAsync(
                                        targetSession,
                                        $"UPDATE {updateIdentity.Literal} MERGE",
                                        op.Entity,
                                        ct).ConfigureAwait(false);
                                }
                                break;

                            case OperationType.Deleted:
                                ResolvedLogger.LogDebug("DELETE {Type} ({Table})", op.EntityType.Name, table);
                                // Fast path: entity has a typed RecordId — preserve it
                                if (op.Entity is IRecord recDel && recDel.Id is not null)
                                {
                                    await DeleteRecordAsync(
                                            op.EntityType,
                                            recDel.Id,
                                            targetSession,
                                            ct).ConfigureAwait(false);
                                    break;
                                }
                                var delId = GetRecordId(op.Entity, table);
                                if (delId is not null)
                                {
                                    await DeleteRecordAsync(
                                        op.EntityType,
                                        delId,
                                        targetSession,
                                        ct).ConfigureAwait(false);
                                }
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
                                if (DocumentIdentityResolver.TryResolve(
                                        op.Entity,
                                        table,
                                        Options.Schema,
                                        out var softDeleteIdentity))
                                {
                                    if (op.Entity is IRecord record)
                                    {
                                        await UpsertRecordAsync(record, softDeleteIdentity.RecordId, targetSession, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await ExecutePocoWriteAsync(
                                            targetSession,
                                            $"UPSERT {softDeleteIdentity.Literal} MERGE",
                                            op.Entity,
                                            ct).ConfigureAwait(false);
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
                                        {
                                            await DeleteRecordAsync(
                                                op.EntityType,
                                                delId,
                                                targetSession,
                                                ct).ConfigureAwait(false);
                                        }
                                    }
                                    else
                                    {
                                        GenerateGuidIdentityIfEmpty(op.Entity);
                                        if (DocumentIdentityResolver.TryResolve(
                                                op.Entity,
                                                table,
                                                Options.Schema,
                                                out var projectedIdentity)
                                            && op.Entity is IRecord record)
                                        {
                                            await UpsertRecordAsync(
                                                record,
                                                projectedIdentity.RecordId,
                                                targetSession,
                                                ct).ConfigureAwait(false);
                                        }
                                        else if (DocumentIdentityResolver.TryResolve(
                                                     op.Entity,
                                                     table,
                                                     Options.Schema,
                                                     out projectedIdentity))
                                        {
                                            await ExecutePocoWriteAsync(
                                                targetSession,
                                                $"UPSERT {projectedIdentity.Literal} MERGE",
                                                op.Entity,
                                                ct).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await CreateEntityAsync(op, table, targetSession, ct).ConfigureAwait(false);
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
                        await patch.ExecuteAsync(this, targetSession, ct).ConfigureAwait(false);
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
                    commitAttempted = true;
                    await tx.Commit(ct).ConfigureAwait(false);
                    MarkVersionFencesCommitted(versionFenceSnapshot);
                }
            }
            catch
            {
                MarkVersionFencesFailed(versionFenceSnapshot);
                if (_explicitTransaction is not null)
                {
                    MarkVersionFencesFailed(_pendingExplicitVersionFences);
                    _explicitTransactionFailed = true;
                }

                if (ownsTx && tx is not null && !commitAttempted)
                {
                    var rolledBack = false;
                    try
                    {
                        await tx.Cancel(CancellationToken.None).ConfigureAwait(false);
                        rolledBack = true;
                    }
                    finally
                    {
                        if (rolledBack)
                            MarkVersionFencesRolledBack(versionFenceSnapshot);
                        if (ReferenceEquals(_pendingAutoEventTransaction, tx))
                        {
                            _appendedEvents.Clear();
                            _fetchForWritingResults.Clear();
                            _unitOfWork.StreamIds.Clear();
                        }
                        if (_explicitTransaction is null)
                            _queuedVersionFences.Clear();
                    }
                }
                if (_explicitTransaction is null)
                    _queuedVersionFences.Clear();
                throw;
            }
            finally
            {
                try
                {
                    if (ownsTx && tx is not null)
                    {
                        try
                        {
                            await tx.DisposeAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            try
                            {
                                if (transactionLease is not null)
                                    await transactionLease.DisposeAsync().ConfigureAwait(false);
                            }
                            finally
                            {
                                if (ReferenceEquals(_pendingAutoEventTransaction, tx))
                                {
                                    _pendingAutoEventTransaction = null;
                                    _pendingAutoEventTransactionLease = null;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _activeSaveSession = null;
                }
            }

            // AfterCommitAsync hooks (called only after successful commit)
            if (_explicitTransaction is not null)
                _pendingExplicitVersionFences.AddRange(versionFenceSnapshot);
            _queuedVersionFences.Clear();

            var committedChanges = new ChangeSet
            {
                Operations = committedOperations,
                AppendedEvents = appendedEventSnapshot,
                Updated = committedOperations.Where(op => op.Type == OperationType.Modified).Select(op => op.Entity).ToArray(),
                Inserted = committedOperations.Where(op => op.Type == OperationType.Added).Select(op => op.Entity).ToArray(),
                Deleted = committedOperations.Where(op => op.Type is OperationType.Deleted or OperationType.SoftDeleted).Select(op => op.Entity).ToArray(),
                VersionFences = versionFenceSnapshot
            };
            if (_explicitTransaction is not null)
                _pendingExplicitChangeSets.Add(committedChanges);
            else
                await InvokeAfterCommitListenersAsync(committedChanges, ct).ConfigureAwait(false);

            var resultCount = count + versionFenceSnapshot.Length;
            if (resultCount == 0)
                resultCount = appendedEventSnapshot.Length + graphOpCount;

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
        catch (Exception ex) when (ex is not ConcurrencyException
                                   && ex is not SableEncryptionException)
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
        finally
        {
            _isSavingChanges = false;
        }
    }

    private async Task InvokeAfterCommitListenersAsync(IChangeSet changes, CancellationToken ct)
    {
        foreach (var listener in Options.Listeners)
            await listener.AfterCommitAsync(this, changes, ct).ConfigureAwait(false);

        foreach (var listener in SessionListeners)
            await listener.AfterCommitAsync(this, changes, ct).ConfigureAwait(false);
    }

    private void GenerateGuidIdentityIfEmpty(object entity)
    {
        var entityType = entity.GetType();
        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var identityProperty = mapping?.IdentityProperty ?? "Id";
        var property = entityType.GetProperty(identityProperty);

        if (property is { CanWrite: true, PropertyType: not null }
            && property.PropertyType == typeof(Guid)
            && property.GetValue(entity) is Guid value
            && value == Guid.Empty)
        {
            property.SetValue(entity, Guid.NewGuid());
        }
    }

    private string? GetEntityId(object entity)
    {
        var table = MetadataDispatch.GetTableName(entity.GetType(), Options.Schema);
        return DocumentIdentityResolver.TryResolve(entity, table, Options.Schema, out var identity)
            ? identity.Key
            : null;
    }

    internal string? GetEntityIdForProtectedOperation(object entity) => GetEntityId(entity);

    internal RecordId? GetRecordIdForProtectedOperation(object entity, string table)
    {
        return DocumentIdentityResolver.TryResolve(entity, table, Options.Schema, out var identity)
            ? identity.RecordId
            : null;
    }

    private RecordId? GetRecordId(object entity, string table)
    {
        return DocumentIdentityResolver.TryResolve(entity, table, Options.Schema, out var identity)
            ? identity.RecordId
            : null;
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
        if (meta is not null && meta.GetIdentityAccessor is not null) return;

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
        var recordId = GetRecordId(entity, table);
        if (recordId is null) return -1;

        var versionField = MetadataDispatch.GetFieldName(
            entityType,
            MetadataDispatch.GetVersionFieldName(entityType)!,
            Options.Schema);
        var surql = $"SELECT {versionField} FROM {DocumentIdentityResolver.FormatRecordIdLiteral(recordId)};";
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);

        response.EnsureAllOks();
        if (response.Count == 0)
            return -1;

        return ReadVersionFromResponse(response, versionField, fallback: -1);
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
        var recordId = GetRecordId(entity, table);
        if (recordId is null)
        {
            ResolvedLogger.LogWarning("Skipping concurrency check for {Type}: unable to resolve entity ID",
                op.EntityType.Name);
            return;
        }
        var id = GetEntityId(entity) ?? DocumentIdentityResolver.FormatRecordIdLiteral(recordId);

        // Query the current version from the DB using a raw SurrealQL call
        // with typed GetValue<T> deserialization (same path as Query provider).
        var versionField = MetadataDispatch.GetFieldName(
            op.EntityType,
            MetadataDispatch.GetVersionFieldName(op.EntityType)!,
            Options.Schema);
        var surql = $"SELECT {versionField} FROM {DocumentIdentityResolver.FormatRecordIdLiteral(recordId)};";
        var response = await session.RawQuery(surql, null, ct).ConfigureAwait(false);

        // A database error must never disable optimistic concurrency. Let the
        // driver surface its typed/generic response exception and fail closed.
        response.EnsureAllOks();

        var dbVersion = ReadVersionFromResponse(response, versionField, fallback: 0);

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

    private static long ReadVersionFromResponse(
        SurrealDbResponse response,
        string versionField,
        long fallback)
    {
        var records = CborResultReader.ReadPocoResult(response, 0);
        if (records.Count == 0)
            return fallback;

        var record = records[0];
        if (!record.TryGetValue(versionField, out var value))
        {
            var actualKey = record.Keys.FirstOrDefault(key =>
                string.Equals(key, versionField, StringComparison.OrdinalIgnoreCase));
            if (actualKey is null)
                return fallback;
            value = record[actualKey];
        }

        if (value is null)
            return fallback;
        if (value is JsonElement element)
        {
            if (element.TryGetInt64(out var number))
                return number;
            if (element.ValueKind == JsonValueKind.String
                && long.TryParse(
                    element.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
            return fallback;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Creates an entity in SurrealDB using <c>Session.Create&lt;T&gt;</c> with the
    /// correct runtime type, ensuring all properties (including version fields)
    /// are serialized by the CBOR serializer.
    /// </summary>
    private async Task<object?> CreateEntityAsync(Operation op, string table, ISurrealDbSession session, CancellationToken ct)
    {
        if (EncryptedFieldResolver.HasEncryptedFields(op.EntityType, Options.Schema))
            throw new SableEncryptedDocumentRequiresIdException(op.EntityType);

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

    /// <summary>
    /// Executes a write for a mapped POCO through Sable's canonical literal serializer.
    /// This keeps normal unit-of-work and inline-projection writes on the same path,
    /// including native <see cref="DateTimeOffset"/> handling.
    /// </summary>
    private async Task ExecutePocoWriteAsync(
        ISurrealDbSession session,
        string statementPrefix,
        object entity,
        CancellationToken ct)
    {
        SurrealDbResponse response;

        var protectedLiteral = await BuildSurrealQlObjectLiteralAsync(entity, ct).ConfigureAwait(false);
        response = await ExecuteRawWriteAsync(
            session,
            $"{statementPrefix} {protectedLiteral.Literal}",
            protectedLiteral.Parameters,
            ct).ConfigureAwait(false);

        ThrowIfRawQueryFailed(
            response,
            EncryptedFieldResolver.HasEncryptedFields(entity.GetType(), Options.Schema));
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

    internal string BuildBulkCreateStatement(object entity, string table)
    {
        GenerateGuidIdentityIfEmpty(entity);
        TryBuildSurrealQlObjectLiteral(entity, out var literal);
        return DocumentIdentityResolver.TryResolve(entity, table, Options.Schema, out var identity)
            ? $"CREATE {identity.Literal} CONTENT {literal};"
            : $"CREATE {table} CONTENT {literal};";
    }

    private async ValueTask<ProtectedSurrealQlLiteral> BuildSurrealQlObjectLiteralAsync(
        object entity,
        CancellationToken cancellationToken)
    {
        var entityType = entity.GetType();
        var recordId = GetEntityId(entity);
        if (string.IsNullOrWhiteSpace(recordId)
            && EncryptedFieldResolver.HasEncryptedFields(entityType, Options.Schema))
        {
            throw new SableEncryptedDocumentRequiresIdException(entityType);
        }

        var protectedValues = await EncryptedDocumentTransformer
            .ProtectFieldsAsync(entity, recordId ?? string.Empty, Options, TenantId, cancellationToken)
            .ConfigureAwait(false);
        var properties = entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true }
                        && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
            .ToArray();
        var mapping = Options.Schema.Mappings.GetValueOrDefault(entityType);
        var identityProperty = mapping?.IdentityProperty ?? "Id";
        var fields = new List<string>(properties.Length);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in properties)
        {
            if (string.Equals(property.Name, identityProperty, StringComparison.OrdinalIgnoreCase))
                continue;

            var fieldName = MetadataDispatch.GetFieldName(entityType, property.Name, Options.Schema);
            if (protectedValues.PropertyOverrides.TryGetValue(property.Name, out var encrypted))
            {
                var parameterName = $"__sable_protected_{parameters.Count}";
                parameters[parameterName] = encrypted;
                fields.Add($"{fieldName}: ${parameterName}");
            }
            else if (TryCreateDeepValueParameter(
                         property.GetValue(entity),
                         parameters,
                         out var parameterName))
            {
                fields.Add($"{fieldName}: ${parameterName}");
            }
            else
            {
                fields.Add($"{fieldName}: {ToSurrealQlLiteral(property.GetValue(entity))}");
            }
        }

        foreach (var additional in protectedValues.AdditionalStorageFields)
        {
            var parameterName = $"__sable_protected_{parameters.Count}";
            parameters[parameterName] = additional.Value;
            fields.Add($"{additional.Key}: ${parameterName}");
        }

        return new ProtectedSurrealQlLiteral(
            "{ " + string.Join(", ", fields) + " }",
            parameters.Count == 0 ? null : parameters);
    }

    /// <summary>
    /// Binds deeply nested document values instead of expanding them into the SurrealQL syntax
    /// tree. SurrealDB applies a parser recursion limit to inline object literals even though the
    /// corresponding stored value is valid document data.
    /// </summary>
    private bool TryCreateDeepValueParameter(
        object? value,
        IDictionary<string, object?> parameters,
        out string parameterName)
    {
        parameterName = string.Empty;
        if (value is null || IsSimpleLiteralValue(value))
            return false;

        JsonElement serialized;
        try
        {
            serialized = JsonSerializer.SerializeToElement(
                value,
                CreateLiteralSerializerOptions());
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (GetMaximumJsonDepth(serialized) <= MaximumInlineLiteralDepth)
            return false;

        parameterName = $"__sable_value_{parameters.Count}";
        parameters[parameterName] = serialized;
        return true;
    }

    private JsonSerializerOptions CreateLiteralSerializerOptions()
    {
        var serializerOptions = Options.SerializerOptions is null
            ? new JsonSerializerOptions(DefaultLiteralSerializerOptions)
            : new JsonSerializerOptions(Options.SerializerOptions);

        if (Options.EnumStorage == EnumStorage.AsString
            && !serializerOptions.Converters.Any(converter =>
                converter is System.Text.Json.Serialization.JsonStringEnumConverter))
        {
            serializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        }

        return serializerOptions;
    }

    private static bool IsSimpleLiteralValue(object value) => value is
        string or char or bool or GeometryPoint or GeometryPolygon or DateTime or DateTimeOffset
        or Guid or Enum or byte or sbyte or short or ushort or int or uint or long or ulong
        or float or double or decimal;

    private static int GetMaximumJsonDepth(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var maximumChildDepth = 0;
            foreach (var property in element.EnumerateObject())
                maximumChildDepth = Math.Max(maximumChildDepth, GetMaximumJsonDepth(property.Value));

            return maximumChildDepth + 1;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var maximumChildDepth = 0;
            foreach (var item in element.EnumerateArray())
                maximumChildDepth = Math.Max(maximumChildDepth, GetMaximumJsonDepth(item));

            return maximumChildDepth + 1;
        }

        return 1;
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
            Guid guid => $"u'{guid:D}'",
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
            .Select(key => $"{(key is string stringKey ? EscapeSurrealQlKey(stringKey) : key)}: {ToSurrealQlLiteral(dictionary[key!])}");

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
        var containsEncryptedEnvelope = surql.Contains(
            "wrapped_key_ciphertext:",
            StringComparison.Ordinal)
            || parameters?.Keys.Any(key =>
                key.StartsWith("__sable_protected_", StringComparison.Ordinal)) == true;
        if (ResolvedLogger.IsEnabled(LogLevel.Debug))
        {
            ResolvedLogger.LogDebug(
                "SaveChangesAsync write SurrealQL: {SurrealQL}{Parameters}",
                containsEncryptedEnvelope ? "<encrypted write redacted>" : surql,
                FormatWriteParameters(parameters));
        }

        var response = await EmbeddedTransactionRawQuery
            .ExecuteAsync(session, surql, parameters, ct)
            .ConfigureAwait(false);

        if (ResolvedLogger.IsEnabled(LogLevel.Debug))
        {
            ResolvedLogger.LogDebug(
                "SaveChangesAsync write response: HasErrors={HasErrors}; Count={Count}; FirstOk={FirstOk}; Errors={Errors}",
                response.HasErrors,
                response.Count,
                response.FirstOk is not null,
                containsEncryptedEnvelope && response.HasErrors
                    ? "<encrypted write error redacted>"
                    : FormatRawQueryErrors(response));
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

    private static void ThrowIfRawQueryFailed(
        SurrealDbResponse response,
        bool redactDetails = false)
    {
        if (!response.HasErrors)
            return;

        var details = redactDetails
            ? "SurrealDB rejected an encrypted write; details were redacted."
            : FormatRawQueryErrors(response);
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
        if (EncryptedFieldResolver.HasEncryptedFields(record.GetType(), Options.Schema))
        {
            if (Options.Schema.Mappings.GetValueOrDefault(record.GetType())?.GetRelationshipMappings().Count > 0)
                throw new SableEncryptedOperationNotSupportedException(record.GetType(), "graph relationship write");

            var encryptedLiteral = await BuildSurrealQlObjectLiteralAsync(record, ct).ConfigureAwait(false);
            var encryptedResponse = await ExecuteRawWriteAsync(
                session,
                $"UPSERT {FormatRecordIdLiteral(rid)} CONTENT {encryptedLiteral.Literal}",
                encryptedLiteral.Parameters,
                ct).ConfigureAwait(false);
            ThrowIfRawQueryFailed(encryptedResponse, redactDetails: true);
            return;
        }
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

    private sealed record ProtectedSurrealQlLiteral(
        string Literal,
        IReadOnlyDictionary<string, object?>? Parameters);

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
        => DocumentIdentityResolver.FormatRecordIdLiteral(tableName, id);

    private static string FormatRecordIdLiteral(RecordId recordId)
        => DocumentIdentityResolver.FormatRecordIdLiteral(recordId);

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
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), Options.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "live query");
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
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), Options.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "live query");
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
        ValidateExplicitTransactionDatabaseTarget(schemaName);

        var targetSession = _explicitTransaction
            ?? await GetWriteSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);

        await ExecuteGraphOperationsAsync(
            targetSession,
            _transactionRelations,
            _transactionUnrelations,
            ct).ConfigureAwait(false);
    }

    internal Task<ISurrealDbSession> GetWriteSessionAsync(Type entityType, CancellationToken ct)
    {
        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(entityType, Options.Schema);
        return GetWriteSessionForSchemaAsync(schemaName, ct);
    }

    private async Task<ISurrealDbSession> GetWriteSessionForSchemaAsync(
        string? schemaName,
        CancellationToken ct)
    {
        if (_explicitTransaction is null)
            return await GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);

        ValidateExplicitTransactionDatabaseTarget(schemaName);

        return _explicitTransaction;
    }

    private void ValidateExplicitTransactionDatabaseTarget(string? schemaName)
    {
        if (_explicitTransaction is null)
            return;

        var transactionDatabase = Options.Database ?? "test";
        var targetDatabase = schemaName ?? transactionDatabase;
        if (string.Equals(targetDatabase, transactionDatabase, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"An explicit transaction is scoped to database '{transactionDatabase}', " +
            $"but this operation targets mapped database '{targetDatabase}'.");
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
            var ridStr = DocumentIdentityResolver.FormatRecordIdLiteral(edgeId);
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
        return await LoadByIdentityAsync<T>(id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> LoadAsync<T>(long id, CancellationToken ct = default) where T : class
    {
        return await LoadByIdentityAsync<T>(id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => LoadByIdentityAsync<T>(id, ct);

    /// <inheritdoc />
    public Task<T?> LoadAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        return LoadByIdentityAsync<T>(id, ct);
    }

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(string id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(int id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(long id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken ct = default) where T : class
        => CheckExistsAsyncCore<T>(id, ct);

    /// <inheritdoc />
    public Task<bool> CheckExistsAsync<T>(object id, CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        return CheckExistsAsyncCore<T>(id, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<string> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyAsync<T>(this, ids, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<Guid> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<long> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<int> ids, CancellationToken ct = default) where T : class
        => LoadManyExtensions.LoadManyByIdentityAsync<T>(this, ids.Cast<object>(), ct);

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
        => Delete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public void Delete<T>(int id) where T : class
        => Delete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public void Delete<T>(Guid id) where T : class
        => Delete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public void Delete<T>(object id) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        Delete(CreateEntityWithId<T>(id));
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
            QueueStoreOperation(doc, OperationType.Insert);
    }

    /// <inheritdoc />
    public void Insert<T>(params T[] documents) where T : class
    {
        foreach (var doc in documents)
            QueueStoreOperation(doc, OperationType.Insert);
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
    public IDocumentVersionFence FenceExpectedVersion<T>(RecordId id, long expectedVersion)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        return FenceExpectedVersionCore<T>(id, expectedVersion);
    }

    /// <inheritdoc />
    public IDocumentVersionFence FenceExpectedVersion<T>(string id, long expectedVersion)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return FenceExpectedVersionCore<T>(id, expectedVersion);
    }

    /// <inheritdoc />
    public IDocumentVersionFence FenceExpectedVersion<T>(long id, long expectedVersion)
        where T : class
        => FenceExpectedVersionCore<T>(id, expectedVersion);

    private IDocumentVersionFence FenceExpectedVersionCore<T>(object id, long expectedVersion)
        where T : class
    {
        if (_isSavingChanges)
        {
            throw new InvalidOperationException(
                "Document version fences cannot be queued while SaveChangesAsync is running.");
        }
        if (_explicitTransactionFailed)
        {
            throw new InvalidOperationException(
                "The active transaction failed a version fence or save operation and cannot accept more changes. " +
                "Roll it back before continuing.");
        }
        if (expectedVersion < 0 || expectedVersion == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion),
                expectedVersion,
                "The expected version must be non-negative and leave room for one increment.");
        }

        var documentType = typeof(T);
        var versionPropertyName = MetadataDispatch.GetVersionFieldName(documentType)
            ?? throw new InvalidOperationException(
                $"Document type '{documentType.FullName}' does not define a version field.");
        ValidateVersionFenceProperty(documentType, versionPropertyName);

        var (database, table) = MetadataDispatch.GetSchemaTarget(documentType, Options.Schema);
        var normalizedId = id is RecordId
            ? id
            : DocumentIdentityResolver.NormalizeForDocumentType(documentType, id, Options.Schema);
        if (!DocumentIdentityResolver.TryCreate(normalizedId, table, out var identity))
        {
            throw new ArgumentException(
                $"The identity for document type '{documentType.FullName}' could not be resolved.",
                nameof(id));
        }
        if (!string.Equals(identity.RecordId.Table, table, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Record table '{identity.RecordId.Table}' does not match mapped table '{table}' " +
                $"for document type '{documentType.FullName}'.",
                nameof(id));
        }

        var tenantField = TryGetTenantMutationField(documentType, out var resolvedTenantField)
            ? resolvedTenantField
            : null;
        var tenantId = tenantField is null ? null : TenantId;
        var existing = _queuedVersionFences
            .Concat(_pendingExplicitVersionFences)
            .FirstOrDefault(fence => fence.Targets(identity.RecordId, database));
        if (existing is not null)
        {
            if (existing.DocumentType != documentType)
            {
                throw new InvalidOperationException(
                    $"Physical record '{DocumentIdentityResolver.FormatRecordIdLiteral(identity.RecordId)}' " +
                    $"is already fenced through document type '{existing.DocumentType.FullName}' and cannot " +
                    $"also be fenced through '{documentType.FullName}'.");
            }
            if (!string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A version fence for '{DocumentIdentityResolver.FormatRecordIdLiteral(identity.RecordId)}' " +
                    "is already queued under a different tenant scope.");
            }
            if (existing.ExpectedVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Conflicting expected versions were supplied for " +
                    $"'{DocumentIdentityResolver.FormatRecordIdLiteral(identity.RecordId)}': " +
                    $"{existing.ExpectedVersion} and {expectedVersion}.");
            }

            return existing;
        }

        var fence = new DocumentVersionFence(
            documentType,
            identity.RecordId,
            expectedVersion,
            database,
            identity.Key,
            MetadataDispatch.GetFieldName(documentType, versionPropertyName, Options.Schema),
            tenantField,
            tenantId);
        AttachTrackedDocument(fence);
        ValidateVersionFenceOperationOverlap([fence]);
        _queuedVersionFences.Add(fence);
        return fence;
    }

    private static void ValidateVersionFenceProperty(Type documentType, string versionPropertyName)
    {
        var property = documentType.GetProperty(
            versionPropertyName,
            BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            throw new InvalidOperationException(
                $"Version field '{versionPropertyName}' was not found on document type '{documentType.FullName}'.");
        }

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (propertyType != typeof(byte)
            && propertyType != typeof(sbyte)
            && propertyType != typeof(short)
            && propertyType != typeof(ushort)
            && propertyType != typeof(int)
            && propertyType != typeof(uint)
            && propertyType != typeof(long)
            && propertyType != typeof(ulong))
        {
            throw new InvalidOperationException(
                $"Version field '{documentType.FullName}.{versionPropertyName}' must use an integer CLR type.");
        }
    }

    private void ValidateVersionFenceOperationOverlap(
        IReadOnlyCollection<DocumentVersionFence> fences)
    {
        if (fences.Count == 0)
            return;

        if (_queuedPatches.Count > 0
            || _queuedStorageOperations.Count > 0
            || QueuedSqlCommands.Count > 0)
        {
            throw new InvalidOperationException(
                "Document version fences cannot be combined with queued patches, raw SQL commands, " +
                "or opaque storage operations because record overlap cannot be proven safe.");
        }
        if ((_appendedEvents.Count > 0 || _fetchForWritingResults.Count > 0)
            && Options.Projections.Any(projection => projection.Lifecycle == ProjectionLifecycle.Inline))
        {
            throw new InvalidOperationException(
                "Document version fences cannot be combined with events that run inline projections " +
                "because projected document overlap cannot be proven safe.");
        }
        if (_unitOfWork.Operations.Count == 0)
            return;

        foreach (var fence in fences)
        {
            foreach (var operation in _unitOfWork.Operations)
            {
                var (database, table) = MetadataDispatch.GetSchemaTarget(
                    operation.EntityType,
                    Options.Schema);
                if (!string.Equals(database, fence.Database, StringComparison.Ordinal))
                    continue;

                var recordId = GetRecordId(operation.Entity, table);
                if (recordId is not null && recordId.Equals(fence.RecordId))
                {
                    throw new InvalidOperationException(
                        $"Document '{DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId)}' " +
                        "cannot be both version-fenced and queued for a document write in the same unit of work.");
                }
            }
        }
    }

    private void ValidateVersionFenceSessionScope(
        IReadOnlyCollection<DocumentVersionFence> fences)
    {
        foreach (var fence in fences)
        {
            if (fence.TenantField is not null
                && !string.Equals(fence.TenantId, TenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The session tenant changed after the version fence for " +
                    $"'{DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId)}' was queued.");
            }
        }
    }

    private async Task ApplyVersionFencesAsync(
        IReadOnlyList<DocumentVersionFence> fences,
        ISurrealDbSession targetSession,
        CancellationToken ct)
    {
        foreach (var fence in fences)
        {
            AttachTrackedDocument(fence);
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["__sable_fence_record"] = fence.RecordId,
                ["__sable_fence_expected"] = fence.ExpectedVersion
            };
            var tenantClause = string.Empty;
            if (fence.TenantField is not null)
            {
                parameters["__sable_fence_tenant"] = fence.TenantId;
                tenantClause = $" AND {fence.TenantField} = $__sable_fence_tenant";
            }

            var surql =
                $"UPDATE $__sable_fence_record SET {fence.VersionField} += 1 " +
                $"WHERE {fence.VersionField} = $__sable_fence_expected{tenantClause} " +
                $"RETURN VALUE {fence.VersionField};";
            var response = await ExecuteRawWriteAsync(
                    targetSession,
                    surql,
                    parameters,
                    ct)
                .ConfigureAwait(false);
            response.EnsureAllOks();

            var versions = response.GetValue<List<long>>(0) ?? [];
            if (versions.Count == 0)
            {
                var actualVersion = await ReadVersionFenceCurrentVersionAsync(
                        fence,
                        targetSession,
                        ct)
                    .ConfigureAwait(false);
                throw new ConcurrencyException(
                    fence.DocumentType,
                    DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId),
                    fence.ExpectedVersion,
                    actualVersion);
            }
            if (versions.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Version fence for '{DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId)}' " +
                    $"returned {versions.Count} rows; exactly one was required.");
            }

            var expectedIncrementedVersion = checked(fence.ExpectedVersion + 1);
            if (versions[0] != expectedIncrementedVersion)
            {
                throw new InvalidOperationException(
                    $"Version fence for '{DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId)}' " +
                    $"returned version {versions[0]}, expected {expectedIncrementedVersion}.");
            }

            fence.MarkApplied(versions[0]);
        }
    }

    private async Task<long> ReadVersionFenceCurrentVersionAsync(
        DocumentVersionFence fence,
        ISurrealDbSession targetSession,
        CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__sable_fence_record"] = fence.RecordId
        };
        var tenantClause = string.Empty;
        if (fence.TenantField is not null)
        {
            parameters["__sable_fence_tenant"] = fence.TenantId;
            tenantClause = $" WHERE {fence.TenantField} = $__sable_fence_tenant";
        }

        var response = await ExecuteRawWriteAsync(
                targetSession,
                $"SELECT VALUE {fence.VersionField} FROM $__sable_fence_record{tenantClause};",
                parameters,
                ct)
            .ConfigureAwait(false);
        response.EnsureAllOks();
        var versions = response.GetValue<List<long>>(0) ?? [];
        if (versions.Count > 1)
        {
            throw new InvalidOperationException(
                $"Version lookup for '{DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId)}' " +
                $"returned {versions.Count} rows; at most one was expected.");
        }
        return versions.Count == 1 ? versions[0] : -1;
    }

    private void MarkVersionFencesCommitted(
        IEnumerable<DocumentVersionFence> fences)
    {
        foreach (var fence in fences)
        {
            fence.MarkCommitted();
            SynchronizeTrackedDocumentVersion(fence);
        }
    }

    private void AttachTrackedDocument(DocumentVersionFence fence)
    {
        if (fence.TrackedDocument is not null)
            return;
        if (!IdentityMap.TryGetValue(fence.DocumentType, out var typeMap))
            return;
        if (typeMap.TryGetValue(fence.IdentityKey, out var document))
            fence.AttachTrackedDocument(document);
    }

    private void SynchronizeTrackedDocumentVersion(DocumentVersionFence fence)
    {
        var document = fence.TrackedDocument;
        var committedVersion = fence.CommittedVersion;
        if (document is null || committedVersion is null)
            return;

        try
        {
            if (MetadataRegistry.TryGet(fence.DocumentType) is ITypeMetadata metadata
                && metadata.SetVersionAccessor is not null)
            {
                metadata.SetVersionAccessor(document, committedVersion.Value);
            }
            else
            {
                var versionPropertyName = MetadataDispatch.GetVersionFieldName(fence.DocumentType)
                    ?? throw new InvalidOperationException(
                        $"Document type '{fence.DocumentType.FullName}' no longer defines a version field.");
                var property = fence.DocumentType.GetProperty(
                    versionPropertyName,
                    BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException(
                        $"Version field '{fence.DocumentType.FullName}.{versionPropertyName}' was not found.");
                var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var convertedVersion = Convert.ChangeType(
                    committedVersion.Value,
                    targetType,
                    CultureInfo.InvariantCulture);
                property.SetValue(document, convertedVersion);
            }

            TrackOriginalVersion(document);
        }
        catch (Exception exception)
        {
            // The storage commit has already succeeded. Never report the transaction as
            // failed solely because a user-defined setter rejected local synchronization;
            // evict the stale instance so subsequent loads cannot return it.
            RemoveOriginalVersion(document);
            if (IdentityMap.TryGetValue(fence.DocumentType, out var typeMap))
                typeMap.TryRemove(fence.IdentityKey, out _);
            ResolvedLogger.LogWarning(
                exception,
                "Committed version fence for {DocumentType} ({RecordId}), but the tracked instance " +
                "could not be synchronized and was evicted",
                fence.DocumentType.FullName,
                DocumentIdentityResolver.FormatRecordIdLiteral(fence.RecordId));
        }
    }

    private static void MarkVersionFencesFailed(
        IEnumerable<DocumentVersionFence> fences)
    {
        foreach (var fence in fences)
            fence.MarkFailed();
    }

    private static void MarkVersionFencesRolledBack(
        IEnumerable<DocumentVersionFence> fences)
    {
        foreach (var fence in fences)
            fence.MarkRolledBack();
    }

    /// <inheritdoc />
    public void UpdateExpectedVersion<T>(T entity, long expectedVersion) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        QueueStoreOperation(entity, OperationType.Modified);
        _expectedVersions[entity] = expectedVersion;
    }

    /// <inheritdoc />
    public void UpdateRevision<T>(T entity, int revision) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        QueueStoreOperation(entity, OperationType.Modified);
        _expectedRevisions[entity] = revision;
    }

    /// <inheritdoc />
    public void TryUpdateRevision<T>(T entity, int revision) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        QueueStoreOperation(entity, OperationType.Modified);
        _expectedRevisions[entity] = revision;
        _tryUpdateRevisions.Add(entity);
    }

    /// <inheritdoc />
    public void Update<T>(IEnumerable<T> documents) where T : class
    {
        foreach (var doc in documents)
            QueueStoreOperation(doc, OperationType.Update);
    }

    /// <inheritdoc />
    public void Update<T>(params T[] documents) where T : class
    {
        foreach (var doc in documents)
            QueueStoreOperation(doc, OperationType.Update);
    }

    /// <inheritdoc />
    public void HardDelete<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        ValidateHardDeleteTenant(entity);
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
        => HardDelete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public void HardDelete<T>(int id) where T : class
        => HardDelete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public void HardDelete<T>(Guid id) where T : class
        => HardDelete(CreateEntityWithId<T>(id));

    /// <inheritdoc />
    public async Task<long> HardDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        EncryptedQueryGuard.Validate(predicate, Options.Schema);
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var (whereClause, parameters) = BuildMutationWhereClause(predicate);
        var sql = $"DELETE FROM {table} WHERE {whereClause};";
        RequestCount++;
        var targetSession = await GetWriteSessionForSchemaAsync(mappedDatabase, ct).ConfigureAwait(false);
        var response = await EmbeddedTransactionRawQuery
            .ExecuteAsync(targetSession, sql, parameters, ct)
            .ConfigureAwait(false);
        response.EnsureAllOks();
        return response.Count;
    }

    /// <inheritdoc />
    public async Task<long> UndoDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class
    {
        EncryptedQueryGuard.Validate(predicate, Options.Schema);
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(typeof(T), Options.Schema);
        var (whereClause, parameters) = BuildMutationWhereClause(predicate);
        var deletedField = MetadataDispatch.GetFieldName(
            typeof(T), nameof(ISoftDeleted.Deleted), Options.Schema);
        var sql = $"UPDATE {table} SET {deletedField} = false " +
            $"WHERE {deletedField} = true AND {whereClause};";
        RequestCount++;
        var targetSession = await GetWriteSessionForSchemaAsync(mappedDatabase, ct).ConfigureAwait(false);
        var response = await EmbeddedTransactionRawQuery
            .ExecuteAsync(targetSession, sql, parameters, ct)
            .ConfigureAwait(false);
        response.EnsureAllOks();
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
    private T CreateEntityWithId<T>(object id) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        var entity = Activator.CreateInstance<T>();
        var mapping = Options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var idPropName = mapping?.IdentityProperty ?? "Id";
        var idProp = typeof(T).GetProperty(idPropName, BindingFlags.Public | BindingFlags.Instance);
        if (idProp is not null && idProp.CanWrite)
        {
            var propType = idProp.PropertyType;
            var actualType = Nullable.GetUnderlyingType(propType) ?? propType;
            var normalizedId = DocumentIdentityResolver.NormalizeForDocumentType(
                typeof(T),
                id,
                Options.Schema);
            object convertedId = normalizedId;
            if (actualType == typeof(RecordId))
            {
                DocumentIdentityResolver.TryCreate(
                    normalizedId,
                    MetadataDispatch.GetTableName(typeof(T), Options.Schema),
                    out var identity);
                convertedId = identity.RecordId;
            }
            else if (actualType.IsGenericType
                     && actualType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
            {
                var recordIdValueType = actualType.GetGenericArguments()[0];
                var recordIdValue = recordIdValueType.IsInstanceOfType(normalizedId)
                    ? normalizedId
                    : Convert.ChangeType(
                        normalizedId,
                        recordIdValueType,
                        System.Globalization.CultureInfo.InvariantCulture);
                convertedId = Activator.CreateInstance(
                    actualType,
                    MetadataDispatch.GetTableName(typeof(T), Options.Schema),
                    recordIdValue)!;
            }
            else if (!actualType.IsInstanceOfType(convertedId))
            {
                convertedId = actualType == typeof(Guid)
                    ? Guid.Parse(Convert.ToString(normalizedId, CultureInfo.InvariantCulture)!)
                    : Convert.ChangeType(
                        normalizedId,
                        actualType,
                        System.Globalization.CultureInfo.InvariantCulture);
            }
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
        if (_pendingAutoEventTransaction is not null)
            await DiscardPendingAutoEventChangesAsync().ConfigureAwait(false);

        if (_explicitTransaction is not null && _ownsTransaction)
        {
            var transaction = _explicitTransaction;
            var rolledBack = false;
            try
            {
                await transaction.Cancel(CancellationToken.None).ConfigureAwait(false);
                rolledBack = true;
            }
            finally
            {
                if (rolledBack)
                {
                    MarkVersionFencesRolledBack(_queuedVersionFences);
                    MarkVersionFencesRolledBack(_pendingExplicitVersionFences);
                }
                else
                {
                    MarkVersionFencesFailed(_queuedVersionFences);
                    MarkVersionFencesFailed(_pendingExplicitVersionFences);
                }
                try
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await ReleaseExplicitTransactionLeaseAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _explicitTransaction = null;
                        _ownsTransaction = false;
                        _explicitTransactionFailed = false;
                        _queuedVersionFences.Clear();
                        _pendingExplicitVersionFences.Clear();
                    }
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps EventStore to track appended events for inline projections.
    /// </summary>
    private sealed class TrackingEventStore : IEvents
    {
        private readonly DocumentSession _owner;

        public TrackingEventStore(DocumentSession owner)
        {
            _owner = owner;
        }

        private IEvents ReadStore => new EventStore(_owner.OperationSession, _owner.Options);

        private bool ShouldStageDirectMutation => _owner.Options.Projections.Count > 0;

        private async Task<T> MutateAsync<T>(Func<IEvents, Task<T>> mutation, CancellationToken ct)
        {
            if (_owner._activeSaveSession is null
                && _owner._explicitTransaction is null
                && _owner._pendingAutoEventTransaction is null
                && !ShouldStageDirectMutation)
            {
                var coordinated = await EmbeddedTransactionCoordinator
                    .BeginAsync(_owner.Session, ct)
                    .ConfigureAwait(false);
                var transaction = coordinated.Transaction;
                var lease = coordinated.Lease;
                var commitAttempted = false;
                try
                {
                    var store = new EventStore(transaction, _owner.Options);
                    var result = await mutation(store).ConfigureAwait(false);
                    commitAttempted = true;
                    await transaction.Commit(ct).ConfigureAwait(false);
                    return result;
                }
                catch
                {
                    if (!commitAttempted)
                        await transaction.Cancel(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    try
                    {
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (lease is not null)
                            await lease.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            try
            {
                var store = new EventStore(await _owner.GetEventMutationSessionAsync(ct).ConfigureAwait(false), _owner.Options);
                return await mutation(store).ConfigureAwait(false);
            }
            catch
            {
                await _owner.DiscardFailedDirectEventMutationAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task MutateAsync(Func<IEvents, Task> mutation, CancellationToken ct)
        {
            await MutateAsync(
                async store =>
                {
                    await mutation(store).ConfigureAwait(false);
                    return true;
                },
                ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<IEvent>> Append(string streamId, IEnumerable<object> events, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.Append(streamId, events, headers, ct), ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.Append(streamId, expectedVersion, events, ct), ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.AppendOptimistic(streamId, lastKnownVersion, events, ct), ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.AppendExclusive(streamId, events, ct), ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.AppendOptimistic(streamId, lastKnownVersion, events, ct), ct).ConfigureAwait(false);
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return result;
        }

        public async Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.AppendExclusive(streamId, events, ct), ct).ConfigureAwait(false);
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
            var result = MutateAsync(store => store.Append(streamId, events, headers: null, ct), ct).GetAwaiter().GetResult();
            foreach (var evt in result)
            {
                _owner._appendedEvents.Add(evt);
                _owner._unitOfWork.StreamIds.Add(evt.StreamId);
            }
            return Task.FromResult(streamId);
        }

        public async Task<FetchForWritingResult<T>> FetchForWritingAsync<T>(string streamId, CancellationToken ct = default) where T : class
        {
            var result = await ReadStore.FetchForWritingAsync<T>(streamId, ct).ConfigureAwait(false);
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
            => ReadStore.AggregateStreamAsync(streamId, version, timestamp, state, fromVersion, ct);

        public Task<StreamState?> FetchStreamStateAsync(string streamId, CancellationToken ct = default)
            => ReadStore.FetchStreamStateAsync(streamId, ct);

        public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken ct = default)
            => ReadStore.FetchStreamStateAsync(streamId, ct);

        public Task<IReadOnlyList<IEvent>> FetchStreamAsync(
            string streamId,
            long? version = null,
            DateTimeOffset? timestamp = null,
            long? fromVersion = null,
            CancellationToken ct = default)
            => ReadStore.FetchStreamAsync(streamId, version, timestamp, fromVersion, ct);

        public Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default)
            => StartStream(streamId, events, ct);

        public Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
            => StartStream(streamId.ToString("D"), events, ct);

        public Task<IReadOnlyList<IEvent>> FetchStream(string streamId, CancellationToken ct = default)
            => ReadStore.FetchStream(streamId, ct);

        public Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
            long sequence, CancellationToken ct = default)
            => ReadStore.FetchAllAfterSequence(sequence, ct);

        public Task ArchiveStream(string streamId, CancellationToken ct = default)
            => MutateAsync(store => store.ArchiveStream(streamId, ct), ct);

        public Task ArchiveStream(Guid streamId, CancellationToken ct = default)
            => MutateAsync(store => store.ArchiveStream(streamId, ct), ct);

        public async Task<IReadOnlyList<IEvent>> WriteTombstone(string streamId, long version, CancellationToken ct = default)
        {
            var result = await MutateAsync(store => store.WriteTombstone(streamId, version, ct), ct).ConfigureAwait(false);
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
            return await ReadStore.BulkInsertEventsAsync(streams, batchSize, ct).ConfigureAwait(false);
        }

        public Task<T?> AggregateStreamToLastKnownAsync<T>(string streamId, CancellationToken ct = default) where T : class
            => ReadStore.AggregateStreamToLastKnownAsync<T>(streamId, ct);

        public Task CompactStreamAsync<T>(string streamId, Action<CompactStreamOptions>? configure = null, CancellationToken ct = default) where T : class
            => ReadStore.CompactStreamAsync<T>(streamId, configure, ct);

        public Task<FetchForWritingResult<T>?> FetchForExclusiveWriting<T>(string streamId, CancellationToken ct = default) where T : class
            => ReadStore.FetchForExclusiveWriting<T>(streamId, ct);

        public ISableQueryable<T> QueryRawEventDataOnly<T>() where T : class
            => ReadStore.QueryRawEventDataOnly<T>();

        public ISableQueryable<IEvent> QueryAllRawEvents()
            => ReadStore.QueryAllRawEvents();

        public IEvent BuildEvent(object data)
            => ReadStore.BuildEvent(data);

        public Task OverwriteEventAsync(IEvent e, CancellationToken ct = default)
            => ReadStore.OverwriteEventAsync(e, ct);

        public Task DeleteSingleEventAsync(string streamId, long eventSequence, CancellationToken ct = default)
            => ReadStore.DeleteSingleEventAsync(streamId, eventSequence, ct);

        public Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken ct = default)
            => ReadStore.EventsExistAsync(query, ct);
    }
}

internal readonly record struct QueuedRelation(
    RecordId From,
    RecordId To,
    Type EdgeType,
    object? Data);
