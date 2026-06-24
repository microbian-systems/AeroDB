using System.Linq.Expressions;

namespace Dali;

/// <summary>
/// Fluent builder for SurrealDB spatial (geo) queries.
/// Supports Nearby, Within, OrderByDistance, filtering, sorting, and pagination.
/// Obtained via <c>session.Spatial&lt;T&gt;()</c>.
/// </summary>
public interface ISpatialQuery<T> where T : class
{
    /// <summary>
    /// Finds documents located within a specified distance from a reference point.
    /// Generates a bounding-box pre-filter + geo::DISTANCE post-filter.
    /// Results are sorted by distance ascending.
    /// </summary>
    ISpatialQuery<T> NearBy(Expression<Func<T, object>> locationField, double latitude, double longitude, double maxDistanceMeters);

    /// <summary>
    /// Finds documents whose geometry point lies within the specified polygon.
    /// Uses the SurrealDB INSIDE operator for containment check.
    /// </summary>
    ISpatialQuery<T> Within(Expression<Func<T, object>> locationField, List<(double Lng, double Lat)> polygon);

    /// <summary>
    /// Sorts results by distance from a reference point (ascending).
    /// </summary>
    ISpatialQuery<T> OrderByDistance(Expression<Func<T, object>> locationField, double latitude, double longitude);

    /// <summary>
    /// Applies a filter condition to the spatial query.
    /// The expression is translated to a SurrealQL WHERE clause.
    /// </summary>
    ISpatialQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Limits the number of results returned.
    /// </summary>
    ISpatialQuery<T> Take(int limit);

    /// <summary>
    /// Skips the specified number of results (for pagination).
    /// </summary>
    ISpatialQuery<T> Skip(int count);

    /// <summary>
    /// Executes the spatial query and returns results.
    /// Each result includes a synthetic _distance field (double, meters from reference point).
    /// </summary>
    Task<List<T>> ToListAsync(CancellationToken ct = default);
}
