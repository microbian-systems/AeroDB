namespace Dali;

/// <summary>
/// Transforms geo-spatial method calls into SurrealQL function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>.
/// </summary>
internal static class GeoExpressionHandler
{
    /// <summary>
    /// Translates a static geo method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "Distance", "Bearing", "Area").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateGeoFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Distance" when args.Length == 3 =>
                $"geo::DISTANCE({args[0]}, ({args[1]}, {args[2]}))",
            "Bearing" when args.Length == 3 =>
                $"geo::BEARING({args[0]}, ({args[1]}, {args[2]}))",
            "Area" when args.Length == 1 =>
                $"geo::AREA({args[0]})",
            "Contains" when args.Length == 2 =>
                $"{args[0]} CONTAINS {args[1]}",
            "Inside" when args.Length == 2 =>
                $"{args[1]} INSIDE {args[0]}",
            _ => null
        };
    }
}
