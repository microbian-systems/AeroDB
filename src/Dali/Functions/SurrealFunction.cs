namespace Dali;

/// <summary>
/// Defines a SurrealDB user-defined function (DEFINE FUNCTION).
/// </summary>
public sealed class SurrealFunction
{
    /// <summary>
    /// The fully qualified function name, e.g. "fn::greet" or "fn::math::double".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The function body as SurrealQL block or expression.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Optional parameter declarations, e.g. "$name: string, $age: int".
    /// </summary>
    public string? Parameters { get; set; }
}


