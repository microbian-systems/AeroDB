namespace Dali;

/// <summary>
/// A pre-compiled LINQ query. Stores the parsed SurrealQL fragments
/// so repeated executions skip expression tree walking.
/// </summary>
public class CompiledQuery<T> where T : class
{
    /// <summary>
    /// The cached translation result produced by <see cref="SurrealExpressionVisitor.Translate"/>.
    /// Immutable from the caller's perspective — execution providers clone before mutating.
    /// </summary>
    internal SurrealQueryResult QueryResult { get; }

    internal CompiledQuery(SurrealQueryResult result)
    {
        QueryResult = result;
    }
}
