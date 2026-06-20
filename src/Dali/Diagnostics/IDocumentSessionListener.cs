namespace Dali;

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

    /// <summary>Called right before a single entity is stored (inserted or updated).</summary>
    void BeforeStore(IDocumentSession session, object entity) { }

    /// <summary>Called right after a single entity has been stored.</summary>
    void AfterStore(IDocumentSession session, object entity) { }

    /// <summary>Called right before a single entity is deleted.</summary>
    void BeforeDelete(IDocumentSession session, object entity) { }

    /// <summary>Called right after a single entity has been deleted.</summary>
    void AfterDelete(IDocumentSession session, object entity) { }
}
