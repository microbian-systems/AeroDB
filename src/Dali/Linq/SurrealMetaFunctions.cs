namespace Dali;

/// <summary>
/// SurrealDB meta functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealMetaFunctions
{
    /// <summary>Returns the record ID. Maps to <c>meta::id()</c>.</summary>
    public static string Id() => throw new NotSupportedException("SurrealMetaFunctions.Id can only be used inside a LINQ expression.");
    /// <summary>Returns the table name. Maps to <c>meta::table()</c>.</summary>
    public static string Table() => throw new NotSupportedException("SurrealMetaFunctions.Table can only be used inside a LINQ expression.");
    /// <summary>Shorthand for table name. Maps to <c>meta::tb()</c>.</summary>
    public static string Tb() => throw new NotSupportedException("SurrealMetaFunctions.Tb can only be used inside a LINQ expression.");
}
