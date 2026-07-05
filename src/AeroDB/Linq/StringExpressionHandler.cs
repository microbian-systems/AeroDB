namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealStringFunctions"/> marker class method calls into SurrealQL string:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType == typeof(SurrealStringFunctions)</c> dispatch.
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
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType == typeof(SurrealStringFunctions))</c> branch
/// in <c>TranslateMethod</c>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. AeroDB's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
internal static class StringExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealStringFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "Repeat", "Reverse", "IsEmail").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateStringFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Repeat" when args.Length == 2 =>
                $"string::repeat({args[0]}, {args[1]})",
            "Reverse" when args.Length == 1 =>
                $"string::reverse({args[0]})",
            "SimilarityFuzzy" when args.Length == 2 =>
                $"string::similarity::fuzzy({args[0]}, {args[1]})",
            "SimilarityJaro" when args.Length == 2 =>
                $"string::similarity::jaro({args[0]}, {args[1]})",
            "SimilarityJaroWinkler" when args.Length == 2 =>
                $"string::similarity::jaro_winkler({args[0]}, {args[1]})",
            "DistanceLevenshtein" when args.Length == 2 =>
                $"string::distance::levenshtein({args[0]}, {args[1]})",
            "DistanceHamming" when args.Length == 2 =>
                $"string::distance::hamming({args[0]}, {args[1]})",
            "DistanceDamerauLevenshtein" when args.Length == 2 =>
                $"string::distance::damerau_levenshtein({args[0]}, {args[1]})",
            "DistanceNormalizedLevenshtein" when args.Length == 2 =>
                $"string::distance::normalized_levenshtein({args[0]}, {args[1]})",
            "DistanceNormalizedDamerauLevenshtein" when args.Length == 2 =>
                $"string::distance::normalized_damerau_levenshtein({args[0]}, {args[1]})",
            "DistanceOsa" when args.Length == 2 =>
                $"string::distance::osa({args[0]}, {args[1]})",
            "Join" when args.Length >= 2 =>
                $"string::join({args[0]}, {string.Join(", ", args.Skip(1))})",
            "IsAlphanum" when args.Length == 1 =>
                $"string::is::alphanum({args[0]})",
            "IsAlpha" when args.Length == 1 =>
                $"string::is::alpha({args[0]})",
            "IsAscii" when args.Length == 1 =>
                $"string::is::ascii({args[0]})",
            "IsEmail" when args.Length == 1 =>
                $"string::is::email({args[0]})",
            "IsUrl" when args.Length == 1 =>
                $"string::is::url({args[0]})",
            "IsUuid" when args.Length == 1 =>
                $"string::is::uuid({args[0]})",
            "IsNumeric" when args.Length == 1 =>
                $"string::is::numeric({args[0]})",
            "IsDatetime" when args.Length == 1 =>
                $"string::is::datetime({args[0]})",
            _ => null
        };
    }
}
