namespace AeroDB.Sable;

/// <summary>
/// Internal interface for patch operations that are queued on the session
/// and executed during <see cref="IDocumentSession.SaveChangesAsync"/>.
/// </summary>
internal interface IDeferredPatch
{
    Task ExecuteAsync(IDocumentSession session, CancellationToken ct);
}
