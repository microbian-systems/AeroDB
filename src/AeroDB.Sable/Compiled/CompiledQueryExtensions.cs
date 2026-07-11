using System.Linq.Expressions;

namespace AeroDB.Sable;

/// <summary>
/// Extension methods for compiling and executing compiled queries.
/// </summary>
public static class CompiledQueryExtensions
{
    /// <summary>
    /// Pre-compiles a LINQ expression into a reusable <see cref="CompiledQuery{T}"/>.
    /// The expression tree is walked once; subsequent executions reuse the cached SurrealQL.
    /// No store initialization or database connection is required — translation is purely local.
    /// </summary>
    /// <param name="store">The document store (used as a logical anchor).</param>
    /// <param name="query">An expression that transforms an <see cref="IQueryable{T}"/> (e.g. q => q.Where(p => p.Age > 25).OrderBy(p => p.Name)).</param>
    /// <typeparam name="T">The record type being queried.</typeparam>
    public static CompiledQuery<T> CompileQuery<T>(
        this IDocumentStore store,
        Expression<Func<IQueryable<T>, IQueryable<T>>> query)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);

        // Use a zero-element in-memory queryable to build the expression tree.
        // No database connection needed — the expression is never executed against it.
        var dummy = Array.Empty<T>().AsQueryable();
        var expression = query.Compile()(dummy).Expression;
        var visitor = new SurrealExpressionVisitor();
        var result = visitor.Translate(expression);
        return new CompiledQuery<T>(result);
    }

    /// <summary>
    /// Executes a compiled query and returns all results as a list.
    /// </summary>
    /// <param name="session">The query session.</param>
    /// <param name="compiled">The compiled query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="T">The record type being queried.</typeparam>
    public static Task<List<T>> QueryAsync<T>(
        this IQuerySession session,
        CompiledQuery<T> compiled,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(compiled);

        var provider = new CompiledQueryProvider<T>(session, compiled);
        return provider.ToListAsync(ct);
    }

    /// <summary>
    /// Executes a compiled query and returns the first result, or <c>default</c> if no results match.
    /// </summary>
    /// <param name="session">The query session.</param>
    /// <param name="compiled">The compiled query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <typeparam name="T">The record type being queried.</typeparam>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this IQuerySession session,
        CompiledQuery<T> compiled,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(compiled);

        var provider = new CompiledQueryProvider<T>(session, compiled);
        return provider.FirstOrDefaultAsync(ct);
    }
}
