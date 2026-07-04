namespace AeroDB;

/// <summary>
/// Represents an explicit SurrealDB transaction started by <see cref="IDocumentSession.BeginTransactionAsync"/>
/// or <see cref="IDocumentSession.BeginTransaction"/>.
/// Supports multiple <see cref="IDocumentSession.SaveChangesAsync"/> calls within a single transaction.
/// Implements <see cref="IAsyncDisposable"/> — rolling back if not committed before disposal.
/// </summary>
public interface IAeroDBTransaction : IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction. After calling this, the session returns to auto-transact mode.
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back the transaction. After calling this, the session returns to auto-transact mode.
    /// </summary>
    Task RollbackAsync(CancellationToken ct = default);
}
