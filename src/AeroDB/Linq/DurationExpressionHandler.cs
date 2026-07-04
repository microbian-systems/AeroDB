namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealDurationFunctions"/> marker class method calls into SurrealQL duration:: function expressions.
/// </summary>
internal static class DurationExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealDurationFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateDurationFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Days" when args.Length == 1 => $"duration::days({args[0]})",
            "Hours" when args.Length == 1 => $"duration::hours({args[0]})",
            "Mins" when args.Length == 1 => $"duration::mins({args[0]})",
            "Secs" when args.Length == 1 => $"duration::secs({args[0]})",
            "Weeks" when args.Length == 1 => $"duration::weeks({args[0]})",
            _ => null
        };
    }
}
