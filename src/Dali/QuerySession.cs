using SurrealDb.Net;

namespace Dali;

public class QuerySession : InternalSessionBase, IQuerySession
{
    public QuerySession(ISurrealDbClient client, ISurrealDbSession session, StoreOptions options)
        : base(client, session, options) { }
}
