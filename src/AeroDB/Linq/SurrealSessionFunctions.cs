namespace AeroDB;

/// <summary>
/// SurrealDB session functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealSessionFunctions
{
    /// <summary>Returns the current session ID. Maps to <c>session::id()</c>.</summary>
    public static string Id() => throw new NotSupportedException("SurrealSessionFunctions.Id can only be used inside a LINQ expression.");
    /// <summary>Returns the current session IP. Maps to <c>session::ip()</c>.</summary>
    public static string Ip() => throw new NotSupportedException("SurrealSessionFunctions.Ip can only be used inside a LINQ expression.");
    /// <summary>Returns the current session origin. Maps to <c>session::origin()</c>.</summary>
    public static string Origin() => throw new NotSupportedException("SurrealSessionFunctions.Origin can only be used inside a LINQ expression.");
    /// <summary>Returns the current namespace. Maps to <c>session::ns()</c>.</summary>
    public static string Ns() => throw new NotSupportedException("SurrealSessionFunctions.Ns can only be used inside a LINQ expression.");
    /// <summary>Returns the current database. Maps to <c>session::db()</c>.</summary>
    public static string Db() => throw new NotSupportedException("SurrealSessionFunctions.Db can only be used inside a LINQ expression.");
    /// <summary>Returns the current scope. Maps to <c>session::sc()</c>.</summary>
    public static string Sc() => throw new NotSupportedException("SurrealSessionFunctions.Sc can only be used inside a LINQ expression.");
    /// <summary>Returns the current token. Maps to <c>session::tk()</c>.</summary>
    public static string Tk() => throw new NotSupportedException("SurrealSessionFunctions.Tk can only be used inside a LINQ expression.");
    /// <summary>Returns the current authenticated user. The most useful session function for auth-gated LINQ queries. Maps to <c>session::user()</c>.</summary>
    public static string User() => throw new NotSupportedException("SurrealSessionFunctions.User can only be used inside a LINQ expression.");
}
