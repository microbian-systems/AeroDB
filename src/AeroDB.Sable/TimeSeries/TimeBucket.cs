namespace AeroDB.Sable;

/// <summary>
/// Predefined time buckets for calendar-aligned grouping via <c>time::group()</c>.
/// </summary>
public enum TimeBucket
{
    Hour,
    Day,
    Week,
    Month,
    Quarter,
    Year
}

/// <summary>
/// Time units for duration-based bucketing via <c>time::floor()</c>.
/// </summary>
public enum TimeUnit
{
    Second,
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year
}

/// <summary>
/// Helper for converting TimeBucket/TimeUnit to SurrealQL bucket expressions.
/// </summary>
internal static class TimeBucketHelper
{
    /// <summary>Converts (value, unit) to SurrealQL duration literal (e.g. "1h", "7d").</summary>
    public static string ToFloorDuration(int value, TimeUnit unit) => unit switch
    {
        TimeUnit.Second => $"{value}s",
        TimeUnit.Minute => $"{value}m",
        TimeUnit.Hour => $"{value}h",
        TimeUnit.Day => $"{value}d",
        TimeUnit.Week => $"{value}w",
        TimeUnit.Month => $"{value}M",
        TimeUnit.Year => $"{value}y",
        _ => $"{value}d"
    };

    /// <summary>Converts TimeBucket to time::group() string argument.</summary>
    public static string ToGroupString(TimeBucket bucket) => bucket switch
    {
        TimeBucket.Hour => "hour",
        TimeBucket.Day => "day",
        TimeBucket.Week => "week",
        TimeBucket.Month => "month",
        TimeBucket.Quarter => "quarter",
        TimeBucket.Year => "year",
        _ => "day"
    };

    /// <summary>Converts a floor bucket to the equivalent group TimeBucket where possible.</summary>
    public static string ToFloorString(TimeBucket bucket) => bucket switch
    {
        TimeBucket.Hour => "1h",
        TimeBucket.Day => "1d",
        TimeBucket.Week => "1w",
        TimeBucket.Month => "1M",
        TimeBucket.Quarter => "3M",
        TimeBucket.Year => "1y",
        _ => "1d"
    };

    /// <summary>
    /// Auto-compute bucket width and unit to achieve approximately targetBucketCount
    /// buckets over the given time range.
    /// Returns (value, unit, floorDuration) for use with time::floor().
    /// </summary>
    public static (int Value, TimeUnit Unit, string FloorDuration) ComputeBuckets(
        DateTime from, DateTime to, int targetBucketCount)
    {
        var span = to - from;
        var bucketWidth = span.TotalSeconds / targetBucketCount;

        if (bucketWidth <= 60) return (1, TimeUnit.Minute, "1m");
        if (bucketWidth <= 3600) return (1, TimeUnit.Hour, "1h");
        if (bucketWidth <= 86400) return (1, TimeUnit.Day, "1d");
        if (bucketWidth <= 604800) return (1, TimeUnit.Week, "1w");
        if (bucketWidth <= 2592000) return (1, TimeUnit.Month, "1M");
        if (bucketWidth <= 7776000) return (3, TimeUnit.Month, "3M");
        return (1, TimeUnit.Year, "1y");
    }
}
