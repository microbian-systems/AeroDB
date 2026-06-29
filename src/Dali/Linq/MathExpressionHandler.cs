namespace Dali;

/// <summary>
/// Transforms <see cref="System.Math"/> method calls into SurrealQL math:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType == typeof(Math)</c> dispatch.
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
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType == typeof(Math))</c> branch
/// in <see cref="SurrealExpressionVisitor.TranslateMethod"/>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. Dali's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
internal static class MathExpressionHandler
{
    /// <summary>
    /// Translates a static <see cref="System.Math"/> method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "Abs", "Ceiling", "Floor", "Round").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateMathFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Abs" when args.Length == 1 =>
                $"math::abs({args[0]})",
            "Ceiling" when args.Length == 1 =>
                $"math::ceil({args[0]})",
            "Floor" when args.Length == 1 =>
                $"math::floor({args[0]})",
            "Round" when args.Length == 1 =>
                $"math::round({args[0]})",
            "Sqrt" when args.Length == 1 =>
                $"math::sqrt({args[0]})",
            "Pow" when args.Length == 2 =>
                $"math::pow({args[0]}, {args[1]})",
            "Min" when args.Length == 2 =>
                $"math::min({args[0]}, {args[1]})",
            "Max" when args.Length == 2 =>
                $"math::max({args[0]}, {args[1]})",
            "Sin" when args.Length == 1 =>
                $"math::sin({args[0]})",
            "Cos" when args.Length == 1 =>
                $"math::cos({args[0]})",
            "Tan" when args.Length == 1 =>
                $"math::tan({args[0]})",
            "Log" when args.Length == 1 =>
                $"math::ln({args[0]})",
            "Log" when args.Length == 2 =>
                $"math::log({args[0]}, {args[1]})",
            "Log10" when args.Length == 1 =>
                $"math::log10({args[0]})",
            "Sign" when args.Length == 1 =>
                $"math::sign({args[0]})",
            _ => null
        };
    }
}
