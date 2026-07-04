namespace Dali;

/// <summary>
/// Transforms <see cref="SurrealObjectFunctions"/> marker class method calls into SurrealQL object:: function expressions.
/// </summary>
internal static class ObjectExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealObjectFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateObjectFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Entries" when args.Length == 1 => $"object::entries({args[0]})",
            "Keys" when args.Length == 1 => $"object::keys({args[0]})",
            "Values" when args.Length == 1 => $"object::values({args[0]})",
            "Len" when args.Length == 1 => $"object::len({args[0]})",
            _ => null
        };
    }
}
