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

    public DocumentSession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options, bool isDirtyTracking)
        : base(client, session, options)
    {
        _isDirtyTracking = isDirtyTracking;
    }

    public IEvents Events => _events ??= new EventStore(Session);

    public void Store<T>(T entity) where T : class
        => _unitOfWork.Add(entity, OperationType.Added);

    public void Delete<T>(T entity) where T : class
        => _unitOfWork.Add(entity, OperationType.Deleted);

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var count = _unitOfWork.Operations.Count;
        if (count == 0) return 0;

        try
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
            return count;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to save changes.", ex);
        }
    }

    private static string? GetEntityId(object entity)
    {
        var prop = entity.GetType().GetProperty("Id");
        return prop?.GetValue(entity)?.ToString();
    }

    private static RecordIdOf<string>? GetRecordId(object entity, string table)
    {
        var id = GetEntityId(entity);
        return string.IsNullOrEmpty(id) ? null : new RecordIdOf<string>(table, id);
    }
}
