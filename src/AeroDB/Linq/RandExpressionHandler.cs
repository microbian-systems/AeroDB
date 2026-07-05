namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealRandFunctions"/> marker class method calls into SurrealQL rand:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType == typeof(SurrealRandFunctions)</c> dispatch.
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
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType == typeof(SurrealRandFunctions))</c> branch
/// in <c>TranslateMethod</c>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. AeroDB's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
internal static class RandExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealRandFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "UuidV4", "UuidV7", "Ulid", "Int", "Float", "String", "Bool", "Guid", "Enum").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateRandFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "UuidV4" when args.Length == 0 => "rand::uuid::v4()",
            "UuidV7" when args.Length == 0 => "rand::uuid::v7()",
            "Ulid" when args.Length == 0 => "rand::ulid()",
            "Int" when args.Length == 2 => $"rand::int({args[0]}, {args[1]})",
            "Float" when args.Length == 2 => $"rand::float({args[0]}, {args[1]})",
            "String" when args.Length == 1 => $"rand::string({args[0]})",
            "Bool" when args.Length == 0 => "rand::bool()",
            "Guid" when args.Length == 0 => "rand::guid()",
            "Enum" when args.Length >= 1 => $"rand::enum({string.Join(", ", args)})",
            _ => null
        };
    }
}
