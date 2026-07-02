namespace Dali;

/// <summary>
/// Transforms geo-spatial method calls into SurrealQL function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType?.Name == "Geo"</c> dispatch.
/// </summary>
/// <remarks>
/// <b>Canonical handler class pattern for SurrealDB built-in function translation.</b>
///
/// Every function category handler follows this contract:
/// <list type="number">
/// <item><c>internal static class XxxExpressionHandler</c></item>
/// <item><c>public static string? TranslateXxxFunc(string methodName, string[] args)</c></item>
/// <item>Returns the SurrealQL expression string for known methods, <c>null</c> for unrecognized</item>
/// </list>
///
/// The visitor chains handlers: if one returns <c>null</c>, the next handler is tried.
/// This allows adding new categories without modifying existing handlers.
///
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType?.Name == "YourCategory")</c> branch
/// in <c>TranslateMethod</c>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. Dali's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
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
            "Intersects" when args.Length == 2 =>
                $"{args[0]} INTERSECTS {args[1]}",
            _ => null
        };
    }
}
