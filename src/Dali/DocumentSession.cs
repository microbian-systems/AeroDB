using System.Text.Json;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class DocumentSession : InternalSessionBase, IDocumentSession
{
    private readonly bool _isDirtyTracking;
    private readonly UnitOfWork _unitOfWork = new();
    private IEvents? _events;
    internal readonly List<(string StreamId, object Event)> _appendedEvents = new();

    public DocumentSession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, bool isDirtyTracking)
        : base(client, session, options)
    {
        _isDirtyTracking = isDirtyTracking;
    }

    public IEvents Events
    {
        get
        {
            if (_events is null)
            {
                var inner = new EventStore(Session);
                _events = new TrackingEventStore(inner, this);
            }
            return _events;
        }
    }

    public void Store<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        // If conjoined tenancy is active and the entity has a TenantId property, set it
        if (!string.IsNullOrEmpty(TenantId))
        {
            var tenantProp = typeof(T).GetProperty("TenantId", typeof(string));
            if (tenantProp is not null && tenantProp.CanWrite)
                tenantProp.SetValue(entity, TenantId);
        }

        _unitOfWork.Add(entity, OperationType.Added);
    }

    public void Delete<T>(T entity) where T : class
    {
        // If conjoined tenancy is active, validate tenant ownership
        if (!string.IsNullOrEmpty(TenantId))
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

        _unitOfWork.Add(entity, OperationType.Deleted);
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

        try
        {
            // Phase 1: persist tracked entities (Added / Modified / Deleted)
            if (count > 0)
            {
                foreach (var op in _unitOfWork.Operations)
                {
                    var table = Snake(op.EntityType.Name);

                    switch (op.Type)
                    {
                        case OperationType.Added:
                            var createdResult = await Session.Create(table, op.Entity, ct);
                            if (createdResult is not null)
                            {
                                var idProp = op.EntityType.GetProperty("Id");
                                var createdId = createdResult.GetType().GetProperty("Id")?.GetValue(createdResult);
                                if (idProp is not null && createdId is not null)
                                    idProp.SetValue(op.Entity, createdId);
                            }
                            break;

                        case OperationType.Modified:
                            var modId = GetRecordId(op.Entity, table);
                            if (modId is not null)
                            {
                                var json = JsonSerializer.Serialize(op.Entity, new JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                                });
                                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
                                await Session.Merge<object>(modId, dict, ct);
                            }
                            break;

                        case OperationType.Deleted:
                            var delId = GetRecordId(op.Entity, table);
                            if (delId is not null)
                                await Session.Delete(delId, ct);
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

                            var context = new ProjectionContext(this, matchingEvents.AsReadOnly());
                            await projection.ApplyAsync(context, ct);
                        }
                    }

                    // Phase 3: persist any projected documents added by inline projections
                    if (_unitOfWork.Operations.Count > 0)
                    {
                        foreach (var op in _unitOfWork.Operations)
                        {
                            var table = Snake(op.EntityType.Name);
                            var entityId = GetEntityId(op.Entity);

                            if (!string.IsNullOrEmpty(entityId) && op.Entity is IRecord record)
                            {
                                // Use SurrealDB's Upsert for create-or-update semantics.
                                // Calling via reflection because the generic type is runtime-only.
                                var rid = new RecordIdOf<string>(table, entityId);
                                await UpsertRecordAsync(record, rid, ct);
                            }
                            else
                            {
                                // No ID set: always create
                                await Session.Create(table, op.Entity, ct);
                            }
                        }

                        _unitOfWork.Clear();
                    }

                    _appendedEvents.Clear();
                }
            }

            return count > 0 ? count : _appendedEvents.Count;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to save changes.", ex);
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
        await (Task)generic.Invoke(Session, [rid, record, ct])!;
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
            await _inner.Append(streamId, list, ct);
            foreach (var evt in list)
                _owner._appendedEvents.Add((streamId, evt));
        }

        public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        {
            var list = events.ToList();
            var result = await _inner.StartStream(streamId, list, ct);
            foreach (var evt in list)
                _owner._appendedEvents.Add((streamId, evt));
            return result;
        }

        public Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default)
            => _inner.FetchStream(streamId, ct);

        public async Task<IReadOnlyList<(string StreamId, object Event, long Version)>> FetchAllAfterVersion(
            long version, CancellationToken ct = default)
            => await _inner.FetchAllAfterVersion(version, ct);
    }
}
