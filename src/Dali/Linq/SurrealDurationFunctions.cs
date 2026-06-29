namespace Dali;

/// <summary>
/// SurrealDB duration functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealDurationFunctions
{
    /// <summary>Extracts days from a duration. Maps to <c>duration::days(dur)</c>.</summary>
    public static long Days(string dur) => throw new NotSupportedException("SurrealDurationFunctions.Days can only be used inside a LINQ expression.");
    /// <summary>Extracts hours from a duration. Maps to <c>duration::hours(dur)</c>.</summary>
    public static long Hours(string dur) => throw new NotSupportedException("SurrealDurationFunctions.Hours can only be used inside a LINQ expression.");
    /// <summary>Extracts minutes from a duration. Maps to <c>duration::mins(dur)</c>.</summary>
    public static long Mins(string dur) => throw new NotSupportedException("SurrealDurationFunctions.Mins can only be used inside a LINQ expression.");
    /// <summary>Extracts seconds from a duration. Maps to <c>duration::secs(dur)</c>.</summary>
    public static long Secs(string dur) => throw new NotSupportedException("SurrealDurationFunctions.Secs can only be used inside a LINQ expression.");
    /// <summary>Extracts weeks from a duration. Maps to <c>duration::weeks(dur)</c>.</summary>
    public static long Weeks(string dur) => throw new NotSupportedException("SurrealDurationFunctions.Weeks can only be used inside a LINQ expression.");
}
