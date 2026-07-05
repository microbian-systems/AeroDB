namespace AeroDB;

/// <summary>A pluggable session factory for creating query and document sessions from a store.</summary>
public interface ISessionFactory
{
    /// <summary>Create a read-only query session.</summary>
    Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default);

    /// <summary>Create a read/write document session.</summary>
    Task<IDocumentSession> OpenSessionAsync(CancellationToken ct = default);

    /// <summary>Create a read/write document session with options.</summary>
    Task<IDocumentSession> OpenSessionAsync(SessionOptions options, CancellationToken ct = default);
}
