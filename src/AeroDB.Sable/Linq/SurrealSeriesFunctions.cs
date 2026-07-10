namespace AeroDB.Sable;

/// <summary>
/// Marker methods for SurrealDB series:: time-series functions.
/// These are never executed — they are intercepted by the LINQ expression visitor
/// and translated to SurrealQL <c>series::*</c> function calls.
/// </summary>
public static class SurrealSeriesFunctions
{
    /// <summary>Adds a value to a time series at the given timestamp. Maps to <c>series::add(series, value, timestamp)</c>.</summary>
    public static double[] Add(double[] series, double value, DateTimeOffset timestamp) => throw new NotSupportedException();
    /// <summary>Returns the number of points in a series. Maps to <c>series::len(series)</c>.</summary>
    public static int Len(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the first value in a series. Maps to <c>series::first(series)</c>.</summary>
    public static double First(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the last value in a series. Maps to <c>series::last(series)</c>.</summary>
    public static double Last(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the minimum value in a series. Maps to <c>series::min(series)</c>.</summary>
    public static double Min(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the maximum value in a series. Maps to <c>series::max(series)</c>.</summary>
    public static double Max(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the mean (average) value in a series. Maps to <c>series::mean(series)</c>.</summary>
    public static double Mean(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the median value in a series. Maps to <c>series::median(series)</c>.</summary>
    public static double Median(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the standard deviation of values in a series. Maps to <c>series::std(series)</c>.</summary>
    public static double Std(double[] series) => throw new NotSupportedException();
    /// <summary>Returns the sum of values in a series. Maps to <c>series::sum(series)</c>.</summary>
    public static double Sum(double[] series) => throw new NotSupportedException();
    /// <summary>Filters a series to a time range. Maps to <c>series::range(series, from, to)</c>.</summary>
    public static double[] Range(double[] series, DateTimeOffset from, DateTimeOffset to) => throw new NotSupportedException();
}
