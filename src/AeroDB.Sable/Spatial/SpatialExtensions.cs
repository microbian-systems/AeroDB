namespace AeroDB.Sable;

/// <summary>
/// Entry point for spatial (geo) queries against SurrealDB geometry fields.
/// </summary>
public static class SpatialExtensions
{
    /// <summary>
    /// Starts a spatial query. Supports nearby, within, and distance-ordered queries.
    /// </summary>
    /// <typeparam name="T">The document type to query.</typeparam>
    /// <param name="session">The query session.</param>
    /// <returns>A fluent spatial query builder.</returns>
    public static ISpatialQuery<T> Spatial<T>(this IQuerySession session) where T : class
    {
        var queryable = session.Query<T>();
        if (queryable.Provider is SurrealQueryProvider provider)
            return new AeroDBSpatialQuery<T>(provider);

        throw new NotSupportedException(
            $"Spatial queries are only supported on AeroDB.Sable query sessions. The current provider is {queryable.Provider.GetType().Name}.");
    }
}
