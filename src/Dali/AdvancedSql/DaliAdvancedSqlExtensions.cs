namespace Dali;

/// <summary>
/// Extensions to expose <see cref="DaliAdvancedSql"/> on session types.
/// </summary>
public static class DaliAdvancedSqlExtensions
{
    /// <summary>
    /// Access advanced SQL capabilities (multi-document tuple queries, streaming).
    /// </summary>
    public static DaliAdvancedSql AdvancedSql(this IQuerySession session)
    {
        if (session is InternalSessionBase s)
            return new DaliAdvancedSql(s);
        throw new InvalidOperationException($"Session type {session.GetType().Name} is not supported.");
    }
}
