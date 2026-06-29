namespace Dali;

/// <summary>
/// Transforms <see cref="SurrealMetaFunctions"/> marker class method calls into SurrealQL meta:: function expressions.
/// </summary>
internal static class MetaExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealMetaFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateMetaFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Id" when args.Length == 0 => "meta::id()",
            "Table" when args.Length == 0 => "meta::table()",
            "Tb" when args.Length == 0 => "meta::tb()",
            _ => null
        };
    }
}
