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

    /// <summary>
    /// Executes a distance-bearing spatial query. This additive extension preserves
    /// the existing <see cref="ISpatialQuery{T}"/> contract and is available for
    /// <see cref="AeroDBSpatialQuery{T}"/> instances created by <see cref="Spatial{T}(IQuerySession)"/>.
    /// </summary>
    public static Task<List<SpatialDistanceResult<T>>> ToListWithDistanceAsync<T>(
        this ISpatialQuery<T> query,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query is AeroDBSpatialQuery<T> spatialQuery
            ? spatialQuery.ExecuteWithDistanceAsync(ct)
            : throw new NotSupportedException("Distance materialization requires an AeroDB.Sable spatial query.");
    }
}
