namespace Dali;

/// <summary>
/// Entry point for time-series queries with temporal bucketing and aggregation.
/// </summary>
public static class TimeSeriesExtensions
{
    /// <summary>
    /// Starts a time-series query. Supports bucketing by time::floor(), time::group(),
    /// aggregation delegation, and downsampling.
    /// </summary>
    /// <typeparam name="T">The document type to query.</typeparam>
    /// <param name="session">The query session.</param>
    /// <returns>A fluent time-series query builder.</returns>
    public static ITimeSeriesQuery<T> TimeSeries<T>(this IQuerySession session) where T : class
    {
        var queryable = session.Query<T>();
        if (queryable.Provider is SurrealQueryProvider provider)
            return new DaliTimeSeriesQuery<T>(provider);

        throw new NotSupportedException(
            $"Time-series queries are only supported on Dali query sessions. The current provider is {queryable.Provider.GetType().Name}.");
    }
}
