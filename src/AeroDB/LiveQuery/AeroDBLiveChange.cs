using SurrealDb.Net.Models.LiveQuery;

namespace AeroDB.LiveQuery;

public sealed record AeroDBLiveChange<T>(
    AeroDBLiveAction Action,
    string? Id,
    T? Document,
    SurrealDbLiveQueryClosureReason? ClosureReason = null);
