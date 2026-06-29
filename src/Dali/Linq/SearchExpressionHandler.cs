namespace Dali;

/// <summary>
/// Transforms <see cref="SurrealFunctions"/> marker class method calls into SurrealQL search:: and vector:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType == typeof(SurrealFunctions)</c> dispatch.
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
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType == typeof(SurrealFunctions))</c> branch
/// in <see cref="SurrealExpressionVisitor.TranslateMethod"/>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. Dali's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
internal static class SearchExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "Score", "VectorDistanceKnn", "VectorSimilarityCosine").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateSearchFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Score" when args.Length == 1 =>
                $"search::score({args[0]})",
            "VectorDistanceKnn" when args.Length == 0 =>
                "vector::distance::knn()",
            "VectorSimilarityCosine" when args.Length == 2 =>
                $"vector::similarity::cosine({args[0]}, {args[1]})",
            "Highlight" when args.Length == 3 =>
                $"search::highlight({args[0]}, {args[1]}, {args[2]})",
            "Offsets" when args.Length == 1 =>
                $"search::offsets({args[0]})",
            "Analyze" when args.Length == 2 =>
                $"search::analyze({args[0]}, {args[1]})",
            "VectorDistanceEuclidean" when args.Length == 2 =>
                $"vector::distance::euclidean({args[0]}, {args[1]})",
            "VectorDistanceManhattan" when args.Length == 2 =>
                $"vector::distance::manhattan({args[0]}, {args[1]})",
            _ => null
        };
    }
}
