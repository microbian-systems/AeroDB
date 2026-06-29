namespace Dali;

/// <summary>
/// Transforms series marker method calls into SurrealQL series:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor"/> via name-based handler dispatch.
/// </summary>
internal static class SeriesExpressionHandler
{
    /// <summary>
    /// Translates a SurrealSeriesFunctions marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateSeriesFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Add" when args.Length == 3 => $"series::add({args[0]}, {args[1]}, {args[2]})",
            "Len" when args.Length == 1 => $"series::len({args[0]})",
            "First" when args.Length == 1 => $"series::first({args[0]})",
            "Last" when args.Length == 1 => $"series::last({args[0]})",
            "Min" when args.Length == 1 => $"series::min({args[0]})",
            "Max" when args.Length == 1 => $"series::max({args[0]})",
            "Mean" when args.Length == 1 => $"series::mean({args[0]})",
            "Median" when args.Length == 1 => $"series::median({args[0]})",
            "Std" when args.Length == 1 => $"series::std({args[0]})",
            "Sum" when args.Length == 1 => $"series::sum({args[0]})",
            "Range" when args.Length == 3 => $"series::range({args[0]}, {args[1]}, {args[2]})",
            _ => null
        };
    }
}
