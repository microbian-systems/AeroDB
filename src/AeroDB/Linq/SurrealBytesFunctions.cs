namespace AeroDB;

/// <summary>
/// SurrealDB bytes functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealBytesFunctions
{
    /// <summary>Returns the length of a byte array. Maps to <c>bytes::len(data)</c>.</summary>
    public static int Len(byte[] data) => throw new NotSupportedException("SurrealBytesFunctions.Len can only be used inside a LINQ expression.");
}
