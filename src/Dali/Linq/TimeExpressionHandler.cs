namespace Dali;

/// <summary>
/// Transforms time-series method calls into SurrealQL function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>.
/// </summary>
internal static class TimeExpressionHandler
{
    /// <summary>
    /// Translates a static time-series method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "Floor", "Group", "Year", "Month").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateTimeFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Floor" when args.Length >= 2 =>
                $"time::floor({args[0]}, {args[1]})",
            "Ceil" when args.Length >= 2 =>
                $"time::ceil({args[0]}, {args[1]})",
            "Round" when args.Length >= 2 =>
                $"time::round({args[0]}, {args[1]})",
            "Group" when args.Length >= 2 =>
                $"time::group({args[0]}, '{args[1]}')",
            "Year" when args.Length == 1 =>
                $"time::year({args[0]})",
            "Month" when args.Length == 1 =>
                $"time::month({args[0]})",
            "Day" when args.Length == 1 =>
                $"time::day({args[0]})",
            "Hour" when args.Length == 1 =>
                $"time::hour({args[0]})",
            "Minute" when args.Length == 1 =>
                $"time::minute({args[0]})",
            "Second" when args.Length == 1 =>
                $"time::second({args[0]})",
            "Week" when args.Length == 1 =>
                $"time::week({args[0]})",
            "Format" when args.Length >= 2 =>
                $"time::format({args[0]}, '{EscapedArg(args[1])}')",
            _ => null
        };
    }

    private static string EscapedArg(string arg) => arg.Trim('\'');
}
