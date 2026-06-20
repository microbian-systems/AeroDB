using SurrealDb.Net;

namespace Dali;

public interface IDaliAdvanced
{
    /// <summary>Direct access to the underlying SurrealDB client.</summary>
    ISurrealDbClient Client { get; }

    /// <summary>Create a new SurrealDB session (for advanced query scenarios).</summary>
    Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default);
}
