namespace AeroDB.Sable;

/// <summary>
/// Logger interface for recording session-level diagnostic information,
/// mirroring the Marten <c>IMartenSessionLogger</c> contract.
/// </summary>
public interface IMartenSessionLogger
{
    void RecordSavedChanges(IDocumentSession session, IChangeSet commit);
    void OnBeforeExecute(IQuerySession session);
    void OnBeforeExecute(IDocumentSession session);
    void LogSuccess(IQuerySession session);
    void LogFailure(Exception ex, IQuerySession session);
    void LogSuccess(IDocumentSession session);
    void LogFailure(Exception ex, IDocumentSession session);
}
