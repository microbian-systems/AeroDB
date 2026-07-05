namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealHttpFunctions"/> marker class method calls into SurrealQL http:: function expressions.
/// </summary>
internal static class HttpExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealHttpFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateHttpFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Get" when args.Length == 1 => $"http::get({args[0]})",
            "Post" when args.Length == 2 => $"http::post({args[0]}, {args[1]})",
            "Put" when args.Length == 2 => $"http::put({args[0]}, {args[1]})",
            "Patch" when args.Length == 2 => $"http::patch({args[0]}, {args[1]})",
            "Delete" when args.Length == 1 => $"http::delete({args[0]})",
            _ => null
        };
    }
}
