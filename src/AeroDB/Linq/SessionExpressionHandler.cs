namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealSessionFunctions"/> marker class method calls into SurrealQL session:: function expressions.
/// </summary>
internal static class SessionExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealSessionFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateSessionFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Id" when args.Length == 0 => "session::id()",
            "Ip" when args.Length == 0 => "session::ip()",
            "Origin" when args.Length == 0 => "session::origin()",
            "Ns" when args.Length == 0 => "session::ns()",
            "Db" when args.Length == 0 => "session::db()",
            "Sc" when args.Length == 0 => "session::sc()",
            "Tk" when args.Length == 0 => "session::tk()",
            "User" when args.Length == 0 => "session::user()",
            _ => null
        };
    }
}
