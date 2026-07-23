namespace AeroDB.Sable;

using SurrealDb.Net;

/// <summary>
/// Internal interface for patch operations that are queued on the session
/// and executed during <see cref="IDocumentSession.SaveChangesAsync"/>.
/// </summary>
internal interface IDeferredPatch
{
    Task ExecuteAsync(
        IDocumentSession session,
        ISurrealDbSession executionSession,
        CancellationToken ct);
}
