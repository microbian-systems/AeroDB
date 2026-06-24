using SurrealDb.Net;

namespace Dali;

/// <summary>Describes the type of change tracked in a document session's unit of work (<c>Added</c>, <c>Modified</c>, <c>Deleted</c>, <c>SoftDeleted</c>).</summary>
public enum OperationType
{
    Added,
    Modified,
    Deleted,
    SoftDeleted
}

public class UnitOfWork
{
    private readonly List<Operation> _operations = new();

    public IReadOnlyList<Operation> Operations => _operations.AsReadOnly();

    public void Add<T>(T entity, OperationType type)
        => _operations.Add(new Operation(entity!, typeof(T), type));

    public void Clear() => _operations.Clear();
}

public class Operation
{
    public Operation(object entity, Type entityType, OperationType type)
    {
        Entity = entity;
        EntityType = entityType;
        Type = type;
    }

    public object Entity { get; }
    public Type EntityType { get; }
    public OperationType Type { get; }
}
