namespace AeroDB;

/// <summary>
/// Listener for projection change batches processed by the <see cref="AsyncDaemon"/>.
/// Analogous to <see cref="IDocumentSessionListener"/> but for daemon-level operations.
/// </summary>
public interface IChangeListener
{
    /// <summary>Called before the daemon commits projected documents.</summary>
    Task BeforeCommitAsync(IDocumentSession session, IReadOnlyList<IProjection> projections, CancellationToken ct);

    /// <summary>Called after the daemon commits projected documents.</summary>
    Task AfterCommitAsync(IDocumentSession session, IReadOnlyList<IProjection> projections, CancellationToken ct, int changes);
}
