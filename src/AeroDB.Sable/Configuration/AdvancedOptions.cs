using SurrealDb.Net;

namespace AeroDB.Sable;

public sealed class AdvancedOptions
{
    /// <summary>Singleton SurrealDB client. Use for admin operations.</summary>
    public ISurrealDbClient? SurrealDbClient { get; internal set; }

    /// <summary>Factory for creating short-lived sessions. Respects tenancy.</summary>
    public Func<CancellationToken, Task<ISurrealDbSession>>? CreateSessionAsync { get; internal set; }
}
