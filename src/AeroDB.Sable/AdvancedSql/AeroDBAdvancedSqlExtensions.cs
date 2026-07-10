namespace AeroDB.Sable;

/// <summary>
/// Extensions to expose <see cref="AeroDBAdvancedSql"/> on session types.
/// </summary>
public static class AeroDBAdvancedSqlExtensions
{
    /// <summary>
    /// Access advanced SQL capabilities (multi-document tuple queries, streaming).
    /// </summary>
    public static AeroDBAdvancedSql AdvancedSql(this IQuerySession session)
    {
        if (session is InternalSessionBase s)
            return new AeroDBAdvancedSql(s);
        throw new InvalidOperationException($"Session type {session.GetType().Name} is not supported.");
    }
}
