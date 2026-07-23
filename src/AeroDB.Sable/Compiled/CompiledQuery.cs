namespace AeroDB.Sable;

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

    /// <summary>
    /// Returns the compiled parameterized command without executing it.
    /// </summary>
    public SableCommand ToCommand()
        => new(QueryResult.ToSurrealQL(), QueryResult.Parameters);

    /// <summary>
    /// Returns the compiled SurrealQL template without inlining parameter values.
    /// </summary>
    public override string ToString() => QueryResult.ToSurrealQL();
}
