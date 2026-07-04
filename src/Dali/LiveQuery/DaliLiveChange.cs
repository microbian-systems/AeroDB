using SurrealDb.Net.Models.LiveQuery;

namespace Dali.LiveQuery;

public sealed record DaliLiveChange<T>(
    DaliLiveAction Action,
    string? Id,
    T? Document,
    SurrealDbLiveQueryClosureReason? ClosureReason = null);
