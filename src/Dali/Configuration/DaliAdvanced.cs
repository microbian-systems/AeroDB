using SurrealDb.Net;

namespace Dali;

internal sealed class DaliAdvanced : IDaliAdvanced
{
    private readonly ISurrealDbClient _client;
    private readonly StoreOptions _options;

    public ISurrealDbClient Client => _client;

    public DaliAdvanced(ISurrealDbClient client, StoreOptions options)
    {
        _client = client;
        _options = options;
    }

    public async Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default)
    {
        // For remote connections, ForkSession. For in-memory, use client directly.
        if (_client is ISurrealDbSession session)
        {
            return await session.ForkSession(ct).ConfigureAwait(false);
        }
        return await _client.CreateSession(ct).ConfigureAwait(false);
    }
}
