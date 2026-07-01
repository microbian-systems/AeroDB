namespace Dali;

/// <summary>Exposes the pending unit-of-work operations before SaveChangesAsync.</summary>
public interface IUnitOfWork
{
    /// <summary>All pending deletions.</summary>
    IReadOnlyList<object> Deletions();

    /// <summary>Pending deletions for type T.</summary>
    IReadOnlyList<T> DeletionsFor<T>() where T : class;

    /// <summary>All pending updates.</summary>
    IReadOnlyList<object> Updates();

    /// <summary>Pending updates for type T.</summary>
    IReadOnlyList<T> UpdatesFor<T>() where T : class;

    /// <summary>All pending inserts.</summary>
    IReadOnlyList<object> Inserts();

    /// <summary>Pending inserts for type T.</summary>
    IReadOnlyList<T> InsertsFor<T>() where T : class;

    /// <summary>All changed documents (inserts + updates).</summary>
    IReadOnlyList<T> AllChangedFor<T>() where T : class;

    /// <summary>All raw IStorageOperation instances.</summary>
    IReadOnlyList<object> Operations();

    /// <summary>Event streams modified in this unit of work.</summary>
    IReadOnlyList<string> Streams();

    /// <summary>All storage operations for a specific document type.</summary>
    IReadOnlyList<object> OperationsFor<T>() where T : class;

    /// <summary>All storage operations for a specific document type (by Type).</summary>
    IReadOnlyList<object> OperationsFor(Type documentType);
}
