namespace AeroDB.Sable;

/// <summary>Convenience access to explicit SurrealDB hashing.</summary>
public static class SurrealHashingExtensions
{
    public static ISurrealHashingService Hashing(this IQuerySession session) =>
        new SurrealHashingService(session);
}
