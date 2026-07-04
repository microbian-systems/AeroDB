namespace Dali;

/// <summary>
/// SurrealDB http functions for use in LINQ expressions.
/// <b>WARNING:</b> These functions execute synchronous HTTP requests from within SurrealDB
/// during query evaluation. Each invocation adds network latency. Using them in
/// <c>Where()</c> predicates may cause one HTTP call per evaluated row.
/// Prefer using these in <c>Select()</c> projections where the call count is bounded.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealHttpFunctions
{
    /// <summary>Performs an HTTP GET request. <b>Makes a synchronous network call per evaluation.</b> Maps to <c>http::get(url)</c>.</summary>
    public static string Get(string url) => throw new NotSupportedException("SurrealHttpFunctions.Get can only be used inside a LINQ expression.");
    /// <summary>Performs an HTTP POST request. <b>Makes a synchronous network call per evaluation.</b> Maps to <c>http::post(url, body)</c>.</summary>
    public static string Post(string url, string body) => throw new NotSupportedException("SurrealHttpFunctions.Post can only be used inside a LINQ expression.");
    /// <summary>Performs an HTTP PUT request. <b>Makes a synchronous network call per evaluation.</b> Maps to <c>http::put(url, body)</c>.</summary>
    public static string Put(string url, string body) => throw new NotSupportedException("SurrealHttpFunctions.Put can only be used inside a LINQ expression.");
    /// <summary>Performs an HTTP PATCH request. <b>Makes a synchronous network call per evaluation.</b> Maps to <c>http::patch(url, body)</c>.</summary>
    public static string Patch(string url, string body) => throw new NotSupportedException("SurrealHttpFunctions.Patch can only be used inside a LINQ expression.");
    /// <summary>Performs an HTTP DELETE request. <b>Makes a synchronous network call per evaluation.</b> Maps to <c>http::delete(url)</c>.</summary>
    public static string Delete(string url) => throw new NotSupportedException("SurrealHttpFunctions.Delete can only be used inside a LINQ expression.");
}
