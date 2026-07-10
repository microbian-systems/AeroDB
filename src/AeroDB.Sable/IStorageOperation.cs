namespace AeroDB.Sable;

/// <summary>
/// Represents a low-level storage operation that can be queued on a session
/// and executed during <see cref="IDocumentSession.SaveChangesAsync"/>.
/// Marten parity: <c>session.QueueOperation(operation)</c>.
/// </summary>
public interface IStorageOperation
{
    /// <summary>
    /// Execute the operation against the given session.
    /// </summary>
    /// <param name="session">The document session to operate on.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteAsync(IDocumentSession session, CancellationToken ct);
}
