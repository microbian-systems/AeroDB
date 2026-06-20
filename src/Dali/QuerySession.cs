using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace Dali;

public class QuerySession : InternalSessionBase, IQuerySession
{
    private readonly ILogger<QuerySession> _logger;

    public QuerySession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options)
        : base(client, session, options)
    {
        _logger = CreateLogger<QuerySession>();
    }
}
