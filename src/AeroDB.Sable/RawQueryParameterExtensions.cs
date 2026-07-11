namespace AeroDB.Sable;

/// <summary>
/// Convenience extension methods for raw SurrealQL queries with positional parameters.
/// Parameters are mapped to <c>$p1</c>, <c>$p2</c>, <c>$p3</c>, etc. in the SQL string.
/// </summary>
public static class RawQueryParameterExtensions
{
    /// <summary>
    /// Executes a raw SurrealQL query with positional parameters.
    /// Parameters are mapped to <c>$p1</c>, <c>$p2</c>, <c>$p3</c>, etc. in the SQL string.
    /// </summary>
    /// <remarks>
    /// This overload does not accept a <see cref="CancellationToken"/>.
    /// For cancellation support, use the dictionary-based overload:
    /// <see cref="IQuerySession.RawQueryAsync{T}(string, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var users = await session.RawQueryAsync&lt;User&gt;(
    ///     "SELECT * FROM user WHERE age &gt; $p1 AND city = $p2", 21, "NYC");
    /// </code>
    /// </example>
    public static Task<List<T>> RawQueryAsync<T>(this IQuerySession session, string sql, params object[] args)
    {
        var dict = new Dictionary<string, object?>(args.Length);
        for (int i = 0; i < args.Length; i++)
            dict[$"p{i + 1}"] = args[i];
        return session.RawQueryAsync<T>(sql, dict, CancellationToken.None);
    }

    /// <summary>
    /// Executes a raw SurrealQL statement with positional parameters.
    /// Parameters are mapped to <c>$p1</c>, <c>$p2</c>, <c>$p3</c>, etc. in the SQL string.
    /// </summary>
    /// <remarks>
    /// This overload does not accept a <see cref="CancellationToken"/>.
    /// For cancellation support, use the dictionary-based overload:
    /// <see cref="IQuerySession.ExecuteSqlAsync(string, IReadOnlyDictionary{string, object?}?, CancellationToken)"/>.
    /// Returns the number of affected records or -1 if unknown.
    /// </remarks>
    /// <example>
    /// <code>
    /// await session.ExecuteSqlAsync(
    ///     "UPDATE user SET age = $p2 WHERE id = $p1", userId, newAge);
    /// </code>
    /// </example>
    public static Task<int> ExecuteSqlAsync(this IQuerySession session, string sql, params object[] args)
    {
        var dict = new Dictionary<string, object?>(args.Length);
        for (int i = 0; i < args.Length; i++)
            dict[$"p{i + 1}"] = args[i];
        return session.ExecuteSqlAsync(sql, dict, CancellationToken.None);
    }
}
