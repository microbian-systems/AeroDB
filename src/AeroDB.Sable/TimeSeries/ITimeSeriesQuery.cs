using System.Linq.Expressions;

namespace AeroDB.Sable;

/// <summary>
/// Fluent builder for SurrealDB time-series queries with temporal bucketing and aggregation.
/// Obtained via <c>session.TimeSeries&lt;T&gt;()</c>.
/// </summary>
public interface ITimeSeriesQuery<T> where T : class
{
    /// <summary>
    /// Buckets results using <c>time::floor()</c> with a flexible duration.
    /// Example: BucketByFloor(x => x.Timestamp, 1, TimeUnit.Hour) → time::floor(Timestamp, 1h)
    /// </summary>
    ITimeSeriesQuery<T> BucketByFloor(Expression<Func<T, object>> timestampField, int value, TimeUnit unit);

    /// <summary>
    /// Buckets results using <c>time::group()</c> for calendar-aligned grouping.
    /// Example: BucketByGroup(x => x.Timestamp, TimeBucket.Month) → time::group(Timestamp, "month")
    /// </summary>
    ITimeSeriesQuery<T> BucketByGroup(Expression<Func<T, object>> timestampField, TimeBucket bucket);

    /// <summary>
    /// Auto-compute bucket width to achieve approximately the target number of buckets.
    /// Requires a time range to be specified via Where() first (or explicit start/end).
    /// </summary>
    ITimeSeriesQuery<T> Downsample(Expression<Func<T, object>> timestampField, int targetBucketCount);

    /// <summary>
    /// Configures aggregate expressions. Delegates to <see cref="AggregateQueryBuilder{T}"/>.
    /// Example: .Select(a => a.Count().As("cnt").Avg(x => x.Value).As("avg_temp"))
    /// </summary>
    ITimeSeriesQuery<T> Select(Action<AggregateQueryBuilder<T>> configure);

    /// <summary>
    /// Applies a filter condition (typically a time range).
    /// The expression is translated to a SurrealQL WHERE clause.
    /// </summary>
    ITimeSeriesQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Limits the number of result rows.
    /// </summary>
    ITimeSeriesQuery<T> Take(int limit);

    /// <summary>
    /// Skips rows (for pagination).
    /// </summary>
    ITimeSeriesQuery<T> Skip(int count);

    /// <summary>
    /// Executes the time-series query and returns results as a list of dynamic records.
    /// </summary>
    Task<List<T>> ToListAsync(CancellationToken ct = default);
}
