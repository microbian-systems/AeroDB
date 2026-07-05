namespace AeroDB;

/// <summary>
/// Lifecycle hooks for document session events. Register via StoreOptions.
/// Multiple listeners are called in registration order.
/// </summary>
public interface IDocumentSessionListener
{
    /// <summary>Called before any entities are persisted in SaveChangesAsync.</summary>
    Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called after all entities have been persisted successfully.</summary>
    Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Called after the SurrealDB transaction has committed successfully.
    /// Receives an <see cref="IChangeSet"/> describing the operations that were committed.
    /// </summary>
    Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called just before the SurrealDB transaction commits.</summary>
    Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called right before a single entity is stored (inserted or updated).</summary>
    Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called right after a single entity has been stored.</summary>
    Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called right before a single entity is deleted.</summary>
    Task BeforeDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Called right after a single entity has been deleted.</summary>
    Task AfterDeleteAsync(IDocumentSession session, object entity, CancellationToken ct) => Task.CompletedTask;
}
