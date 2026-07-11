namespace AeroDB.Sable;

/// <summary>
/// SurrealDB object functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// <br/>
/// Note: <c>object::merge</c>, <c>object::omit</c>, <c>object::pick</c>, <c>object::set</c>
/// are not available in the current SurrealDB version. Add these when a newer version confirms availability.
/// </summary>
public static class SurrealObjectFunctions
{
    /// <summary>Returns entries of an object. Maps to <c>object::entries(obj)</c>.</summary>
    public static object[] Entries(object obj) => throw new NotSupportedException("SurrealObjectFunctions.Entries can only be used inside a LINQ expression.");
    /// <summary>Returns keys of an object. Maps to <c>object::keys(obj)</c>.</summary>
    public static string[] Keys(object obj) => throw new NotSupportedException("SurrealObjectFunctions.Keys can only be used inside a LINQ expression.");
    /// <summary>Returns values of an object. Maps to <c>object::values(obj)</c>.</summary>
    public static object[] Values(object obj) => throw new NotSupportedException("SurrealObjectFunctions.Values can only be used inside a LINQ expression.");
    /// <summary>Returns the length of an object. Maps to <c>object::len(obj)</c>.</summary>
    public static int Len(object obj) => throw new NotSupportedException("SurrealObjectFunctions.Len can only be used inside a LINQ expression.");
}
