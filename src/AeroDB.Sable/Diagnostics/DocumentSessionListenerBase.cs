namespace AeroDB.Sable;

/// <summary>
/// Base class for <see cref="IDocumentSessionListener"/> that provides no-op
/// default implementations for all hooks. Subclasses can override only the
/// hooks they care about. Use pattern matching in overrides to handle specific entity types.
/// </summary>
/// <example>
/// <code>
/// public class OrderAuditListener : DocumentSessionListenerBase
/// {
///     public override Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
///     {
///         if (entity is Order order)
///         {
///             // audit logic for Order entities
///         }
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
public abstract class DocumentSessionListenerBase : IDocumentSessionListener
{
    public virtual Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task BeforeDeleteAsync(IDocumentSession session, object entity, CancellationToken ct)
        => Task.CompletedTask;

    public virtual Task AfterDeleteAsync(IDocumentSession session, object entity, CancellationToken ct)
        => Task.CompletedTask;
}
