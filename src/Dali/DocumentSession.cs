using System.Reflection;
using System.Text.Json;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class DocumentSession : InternalSessionBase, IDocumentSession
{
    private readonly ILogger<DocumentSession> _logger;
    private readonly bool _isDirtyTracking;
    private readonly UnitOfWork _unitOfWork = new();
    private IEvents? _events;
    internal readonly List<(string StreamId, object Event)> _appendedEvents = new();

    /// <summary>
    /// Cached <c>MethodInfo</c> for <see cref="SurrealDbResponse.GetValue{T}"/>,
    /// used by <see cref="CheckConcurrencyAsync"/> to avoid reflection lookup on every call.
    /// </summary>
    private static readonly MethodInfo? GetValueMethod = typeof(SurrealDbResponse).GetMethods()
        .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition);

    public DocumentSession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, bool isDirtyTracking)
        : base(client, session, options)
    {
        _isDirtyTracking = isDirtyTracking;
        _logger = CreateLogger<DocumentSession>();
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

    public void Store<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        // If conjoined tenancy is active and the entity has a TenantId property, set it
        // DatabasePerTenant isolates at the database level — no entity-level tenant ID needed.
        if (!string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
        {
            var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
            if (tenantProp is not null && tenantProp.CanWrite)
                tenantProp.SetValue(entity, TenantId);
        }

        // Track original version for optimistic concurrency
        if (Options.UseOptimisticConcurrency)
            TrackOriginalVersion(entity);

        _unitOfWork.Add(entity, OperationType.Added);
        _logger.LogDebug("Stored {Type} for {Operation}", typeof(T).Name, OperationType.Added);
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

        // If conjoined tenancy is active, validate tenant ownership
        // DatabasePerTenant isolates at the database level — no entity-level tenant check needed.
        if (!string.IsNullOrEmpty(TenantId) && Options.TenancyStyle == TenancyStyle.Conjoined)
        {
            var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
            if (tenantProp is not null && tenantProp.CanRead)
            {
                var entityTenant = tenantProp.GetValue(entity) as string;
                if (!string.Equals(entityTenant, TenantId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Cannot delete entity belonging to tenant '{entityTenant}' " +
                        $"from a session scoped to tenant '{TenantId}'.");
                }
            }
        }

        // If the entity implements ISoftDeleted, queue a soft-delete instead
        if (entity is ISoftDeleted)
        {
            _unitOfWork.Add(entity, OperationType.SoftDeleted);
            _logger.LogDebug("Queued {Type} for soft-deletion", typeof(T).Name);
        }
        else
        {
            _unitOfWork.Add(entity, OperationType.Deleted);
            _logger.LogDebug("Queued {Type} for deletion", typeof(T).Name);
        }
    }

    public void ClearChanges()
    {
        _unitOfWork.Clear();
        _appendedEvents.Clear();
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var count = _unitOfWork.Operations.Count;
        if (count == 0 && _appendedEvents.Count == 0) return 0;

        _logger.LogInformation("SaveChangesAsync: committing {EntityCount} entities and {EventCount} events",
            count, _appendedEvents.Count);

        try
        {
            // Phase 1: Optimistic concurrency checks (Modified entities only)
            // Runs before any mutations so we fail-fast if a conflict exists.
            if (Options.UseOptimisticConcurrency && count > 0)
            {
                foreach (var op in _unitOfWork.Operations)
                {
                    if (op.Type == OperationType.Modified)
                        await CheckConcurrencyAsync(op, ct).ConfigureAwait(false);
                }
            }

            // Phase 2: Increment version fields on all entities before persisting
            if (Options.UseOptimisticConcurrency && count > 0)
            {
                foreach (var op in _unitOfWork.Operations)
                {
                    if (op.Type is OperationType.Added or OperationType.Modified)
                        IncrementVersion(op.Entity);
                }
            }

            // Phase 3: persist tracked entities (Added / Modified / Deleted)
            if (count > 0)
            {
                foreach (var op in _unitOfWork.Operations)
                {
                    var table = MetadataDispatch.GetTableName(op.EntityType);

                    switch (op.Type)
                    {
                        case OperationType.Added:
                            _logger.LogDebug("CREATE {Type} ({Table})", op.EntityType.Name, table);
                            var createdResult = await CreateEntityAsync(op, table, ct).ConfigureAwait(false);
                            if (createdResult is not null)
                            {
                                var idProp = op.EntityType.GetProperty("Id");
                                var createdId = createdResult.GetType().GetProperty("Id")?.GetValue(createdResult);
                                if (idProp is not null && createdId is not null)
                                    idProp.SetValue(op.Entity, createdId);
                            }
                            break;

                        case OperationType.Modified:
                            _logger.LogDebug("UPDATE {Type} ({Table})", op.EntityType.Name, table);
                            var modId = GetRecordId(op.Entity, table);
                            if (modId is not null)
                            {
                                if (op.Entity is IRecord record)
                                {
                                    // Use Upsert (create-or-update) via the SDK's typed path,
                                    // which avoids CBOR serialization issues with JsonElement values.
                                    await UpsertRecordAsync(record, modId, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    // Fall back to Merge for non-Record types
                                    var json = JsonSerializer.Serialize(op.Entity, new JsonSerializerOptions
                                    {
                                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                                    });
                                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
                                    await Session.Merge<object>(modId, dict, ct).ConfigureAwait(false);
                                }
                            }
                            break;

                        case OperationType.Deleted:
                            _logger.LogDebug("DELETE {Type} ({Table})", op.EntityType.Name, table);
                            var delId = GetRecordId(op.Entity, table);
                            if (delId is not null)
                                await Session.Delete(delId, ct).ConfigureAwait(false);
                            break;

                        case OperationType.SoftDeleted:
                            _logger.LogDebug("SOFT-DELETE {Type} ({Table})", op.EntityType.Name, table);
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
                                    await UpsertRecordAsync(record, softDelId, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    var json = JsonSerializer.Serialize(op.Entity, new JsonSerializerOptions
                                    {
                                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                                    });
                                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
                                    await Session.Merge<object>(softDelId, dict, ct).ConfigureAwait(false);
                                }
                            }
                            break;
                    }
                }

                _unitOfWork.Clear();
            }

            // Phase 2: run inline projections on events appended during this session
            if (_appendedEvents.Count > 0 && Options.Projections.Count > 0)
            {
                var inlineProjections = Options.Projections
                    .Where(p => p.Lifecycle == ProjectionLifecycle.Inline)
                    .ToList();

                if (inlineProjections.Count > 0)
                {
                    // Group appended events by stream
                    var streamGroups = _appendedEvents
                        .GroupBy(e => e.StreamId)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.Event).ToList());

                    foreach (var projection in inlineProjections)
                    {
                        foreach (var (streamId, events) in streamGroups)
                        {
                            var matchingEvents = events
                                .Where(e => projection.EventTypes.Contains(e.GetType()))
                                .ToList();

                            if (matchingEvents.Count == 0)
                                continue;

                            _logger.LogInformation("Inline projection {ProjectionType} applied on stream {StreamId}",
                                projection.GetType().Name, streamId);

                            var context = new ProjectionContext(this, matchingEvents.AsReadOnly());
                            await projection.ApplyAsync(context, ct).ConfigureAwait(false);
                        }
                    }

                    // Phase 3: persist any projected documents added by inline projections
                    if (_unitOfWork.Operations.Count > 0)
                    {
                        foreach (var op in _unitOfWork.Operations)
                        {
                            var table = MetadataDispatch.GetTableName(op.EntityType);
                            var entityId = GetEntityId(op.Entity);

                            if (!string.IsNullOrEmpty(entityId) && op.Entity is IRecord record)
                            {
                                // Use SurrealDB's Upsert for create-or-update semantics.
                                // Calling via reflection because the generic type is runtime-only.
                                var rid = new RecordIdOf<string>(table, entityId);
                                await UpsertRecordAsync(record, rid, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                // No ID set: always create
                                await Session.Create(table, op.Entity, ct).ConfigureAwait(false);
                            }
                        }

                        _unitOfWork.Clear();
                    }

                    _appendedEvents.Clear();
                }
            }

            var resultCount = count > 0 ? count : _appendedEvents.Count;
            _logger.LogInformation("SaveChangesAsync: committed {Count} changes", resultCount);
            return resultCount;
        }
        catch (Exception ex) when (ex is not ConcurrencyException)
        {
            _logger.LogError(ex, "SaveChangesAsync failed");
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
        var prop = entity.GetType().GetProperty("Id");
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
    /// are two separate SurrealDB operations — they are <em>not</em> wrapped in a single
    /// transaction. SurrealDB's record-level locking reduces the practical window for races,
    /// but concurrent conflicting writes are theoretically possible under extreme contention.
    /// Applications that require strict serializable isolation should consider external
    /// coordination mechanisms (e.g. distributed locks).
    /// </para>
    /// </summary>
    private async Task CheckConcurrencyAsync(Operation op, CancellationToken ct)
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
            _logger.LogWarning("Skipping concurrency check for {Type}: unable to resolve entity ID",
                op.EntityType.Name);
            return;
        }

        // Query the current version from the DB using a raw SurrealQL call
        // with typed GetValue<T> deserialization (same path as Query provider).
        var surql = $"SELECT * FROM {table}:{id};";
        var response = await Session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors)
        {
            _logger.LogWarning("Skipping concurrency check for {Type}/{Id}: RawQuery returned errors",
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
            _logger.LogWarning(
                "Concurrency conflict on {Type} (id={Id}): expected version {Expected}, found {Actual}",
                op.EntityType.Name, id, expectedVersion, dbVersion);

            throw new ConcurrencyException(op.EntityType, id, expectedVersion, dbVersion);
        }

        _logger.LogDebug("Concurrency check passed for {Type} (id={Id}): version {Version}",
            op.EntityType.Name, id, dbVersion);
    }

    /// <summary>
    /// Creates an entity in SurrealDB using <c>Session.Create&lt;T&gt;</c> with the
    /// correct runtime type, ensuring all properties (including version fields)
    /// are serialized by the CBOR serializer.
    /// </summary>
    private async Task<object?> CreateEntityAsync(Operation op, string table, CancellationToken ct)
    {
        // Find the generic Create<T>(string, T, CancellationToken) method
        var createMethod = typeof(ISurrealDbSession).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ISurrealDbSession.Create)
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 3
                && m.GetParameters()[0].ParameterType == typeof(string)
                && m.GetParameters()[2].ParameterType == typeof(CancellationToken));

        if (createMethod is null)
        {
            // Fallback to untyped Create
            return await Session.Create(table, op.Entity, ct).ConfigureAwait(false);
        }

        var genericCreate = createMethod.MakeGenericMethod(op.EntityType);
        var task = (Task?)genericCreate.Invoke(Session, [table, op.Entity, ct]);
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

    private async Task UpsertRecordAsync(IRecord record, RecordId rid, CancellationToken ct)
    {
        // Use the Upsert method via ISurrealDbSharedMethods interface.
        // We call the generic method with the record's runtime type.
        var entityType = record.GetType();
        var upsertMethod = typeof(ISurrealDbSharedMethods).GetMethods()
            .First(m => m.Name == nameof(ISurrealDbSharedMethods.Upsert)
                        && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(RecordId));
        var generic = upsertMethod.MakeGenericMethod(entityType, entityType);
        var task = (Task)generic.Invoke(Session, [rid, record, ct])!;
        await task.ConfigureAwait(false);
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

        public async Task Append(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var list = events.ToList();
            await _inner.Append(streamId, list, ct).ConfigureAwait(false);
            foreach (var evt in list)
                _owner._appendedEvents.Add((streamId, evt));
        }

        public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var list = events.ToList();
            var result = await _inner.StartStream(streamId, list, ct).ConfigureAwait(false);
            foreach (var evt in list)
                _owner._appendedEvents.Add((streamId, evt));
            return result;
        }

        public Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default)
            => _inner.FetchStream(streamId, ct);

        public async Task<IReadOnlyList<(string StreamId, object Event, long Version)>> FetchAllAfterVersion(
            long version, CancellationToken ct = default)
            => await _inner.FetchAllAfterVersion(version, ct).ConfigureAwait(false);
    }
}
