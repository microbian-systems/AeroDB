namespace Dali;

/// <summary>
/// Query diagnostics: preview generated SurrealQL and explain plans.
/// Access via <c>store.Advanced.Diagnostics</c>.
/// </summary>
public interface IDiagnostics
{
    /// <summary>Generate the SurrealQL for a query without executing it.</summary>
    Task<string> PreviewCommandAsync<T>(IQueryable<T> query, CancellationToken ct = default) where T : class;

    /// <summary>Get the SurrealDB EXPLAIN output for a query.</summary>
    Task<string> ExplainPlanAsync<T>(IQueryable<T> query, CancellationToken ct = default) where T : class;
}
