namespace AeroDB.Sable;

/// <summary>
/// Optional enrichment hook for projections. Called before each projection invocation.
/// Use <see cref="IQuerySession"/> to load reference data needed by the projection.
/// </summary>
public interface IEnrichProjection
{
    /// <summary>
    /// Enrich the projection context before events are processed.
    /// Use the query session to pre-load data.
    /// </summary>
    Task EnrichAsync(IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken ct);
}
