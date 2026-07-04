namespace Dali;

/// <summary>Describes the type of change tracked in a document session's unit of work (<c>Added</c>, <c>Modified</c>, <c>Deleted</c>, <c>SoftDeleted</c>, <c>Insert</c>, <c>Update</c>).</summary>
public enum OperationType
{
    Added,
    Modified,
    Deleted,
    SoftDeleted,
    Insert,
    Update
}

/// <summary>Tracks pending document operations for a document session. Implements <see cref="IUnitOfWork"/> to expose pending changes for inspection.</summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly List<Operation> _operations = new();
    private readonly List<string> _streamIds = new();

    public IReadOnlyList<Operation> Operations => _operations.AsReadOnly();
    internal IList<string> StreamIds => _streamIds;

    public void Add<T>(T entity, OperationType type)
        => _operations.Add(new Operation(entity!, typeof(T), type));

    public void Clear()
    {
        _operations.Clear();
        _streamIds.Clear();
    }

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.Deletions()
        => _operations.Where(op => op.Type is OperationType.Deleted or OperationType.SoftDeleted).Select(op => op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<T> IUnitOfWork.DeletionsFor<T>()
        => _operations.Where(op => op.Type is OperationType.Deleted or OperationType.SoftDeleted && op.EntityType == typeof(T)).Select(op => (T)op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.Updates()
        => _operations.Where(op => op.Type == OperationType.Modified || op.Type == OperationType.Update).Select(op => op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<T> IUnitOfWork.UpdatesFor<T>()
        => _operations.Where(op => (op.Type == OperationType.Modified || op.Type == OperationType.Update) && op.EntityType == typeof(T)).Select(op => (T)op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.Inserts()
        => _operations.Where(op => op.Type is OperationType.Added or OperationType.Insert).Select(op => op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<T> IUnitOfWork.InsertsFor<T>()
        => _operations.Where(op => (op.Type is OperationType.Added or OperationType.Insert) && op.EntityType == typeof(T)).Select(op => (T)op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<T> IUnitOfWork.AllChangedFor<T>()
        => _operations.Where(op => op.Type is OperationType.Added or OperationType.Modified or OperationType.Insert or OperationType.Update && op.EntityType == typeof(T)).Select(op => (T)op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.Operations()
        => _operations.Select(op => op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<string> IUnitOfWork.Streams()
        => _streamIds.AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.OperationsFor<T>()
        => _operations.Where(op => op.EntityType == typeof(T)).Select(op => op.Entity).ToList().AsReadOnly();

    /// <inheritdoc />
    IReadOnlyList<object> IUnitOfWork.OperationsFor(Type documentType)
        => _operations.Where(op => op.EntityType == documentType).Select(op => op.Entity).ToList().AsReadOnly();

    /// <summary>Remove all pending operations for the given entity (by reference).</summary>
    public void Eject<T>(T entity)
    {
        _operations.RemoveAll(op => ReferenceEquals(op.Entity, entity));
    }

    /// <summary>Remove all pending operations for the given document type.</summary>
    public void EjectAllOfType(Type type)
    {
        _operations.RemoveAll(op => op.EntityType == type);
    }
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
