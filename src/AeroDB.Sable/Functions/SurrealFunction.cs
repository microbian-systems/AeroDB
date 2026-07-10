namespace AeroDB.Sable;

/// <summary>Typed parameter definition for a SurrealDB user-defined function.</summary>
public sealed class SurrealFunctionParameter
{
    /// <summary>The parameter name (without the leading <c>$</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The SurrealQL data type, e.g. <c>"string"</c>, <c>"int"</c>, <c>"float"</c>, <c>"record&lt;person&gt;"</c>.</summary>
    public string Type { get; set; } = "string";

    /// <summary>When true the parameter is optional and can be omitted at the call site.</summary>
    public bool IsOptional { get; set; }
}

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
    /// Optional raw parameter string, e.g. "$name: string, $age: int". Used as fallback if ParametersTyped list is empty.
    /// </summary>
    public string? Parameters { get; set; }

    /// <summary>Typed parameter definitions. Takes precedence over Parameters string when non-empty.</summary>
    public List<SurrealFunctionParameter> ParametersTyped { get; set; } = new();
}


