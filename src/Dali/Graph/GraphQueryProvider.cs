namespace Dali;

internal static class GraphQueryProvider
{
    /// <summary>Start a graph traversal query from the given session.</summary>
    public static IGraphQuery<T> Graph<T>(IQuerySession session) where T : class
    {
        // Extract the underlying ISurrealDbSession if available.
        // InternalSessionBase exposes Session; use it for raw query access.
        var rawSession = GetRawSession(session);
        return new GraphQueryBuilder<T>(session, rawSession, null);
    }

    /// <summary>Start a graph traversal query with a SurrealQL WHERE filter.</summary>
    public static IGraphQuery<T> Graph<T>(IQuerySession session, string filterSurql) where T : class
    {
        var rawSession = GetRawSession(session);
        return new GraphQueryBuilder<T>(session, rawSession, filterSurql);
    }

    /// <summary>
    /// Resolves the underlying <c>ISurrealDbSession</c> from an <c>IQuerySession</c>.
    /// InternalSessionBase (the base class of DocumentSession and QuerySession) exposes
    /// Session as a public property, so we can cast to access it.
    /// </summary>
    internal static SurrealDb.Net.ISurrealDbSession GetRawSession(IQuerySession session)
    {
        if (session is InternalSessionBase internalBase)
            return internalBase.Session;

        // Fallback: try reflection to find a Session property
        var sessionProp = session.GetType().GetProperty("Session",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (sessionProp?.GetValue(session) is SurrealDb.Net.ISurrealDbSession raw)
            return raw;

        throw new InvalidOperationException(
            $"Cannot resolve ISurrealDbSession from {session.GetType().Name}. " +
            "Graph queries require a session that exposes the underlying SurrealDB session.");
    }
}
