namespace AeroDB;

/// <summary>
/// Transforms <see cref="SurrealBytesFunctions"/> marker class method calls into SurrealQL bytes:: function expressions.
/// </summary>
internal static class BytesExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealBytesFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateBytesFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Len" when args.Length == 1 => $"bytes::len({args[0]})",
            _ => null
        };
    }
}
