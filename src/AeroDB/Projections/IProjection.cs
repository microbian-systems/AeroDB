using Microsoft.Extensions.Logging;

namespace AeroDB;

/// <summary>
/// Defines a projection that builds read-side documents from events.
/// </summary>
public interface IProjection
{
    /// <summary>
    /// Event types this projection handles (by C# type).
    /// </summary>
    Type[] EventTypes { get; }

    /// <summary>
    /// Whether the projection runs inline during SaveChangesAsync or asynchronously.
    /// </summary>
    ProjectionLifecycle Lifecycle { get; }

    /// <summary>
    /// Apply the projection logic to the given events.
    /// </summary>
    Task ApplyAsync(IProjectionContext context, CancellationToken ct);

    /// <summary>
    /// Projection name for identification in logs and progress tracking.
    /// Defaults to the type name when not explicitly set.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Rebuild the projection by replaying ALL events from the event store.
    /// Used for initial projection creation or recovery after data loss.
    /// The projection is responsible for handling idempotency — calling
    /// this twice should produce the same result.
    /// </summary>
    Task RebuildAsync(IDocumentSession session, CancellationToken ct);

    /// <summary>
    /// Enrichment hook called before <see cref="ApplyAsync"/>. Projections can pre-load
    /// reference data or the aggregate document. Default is no-op.
    /// </summary>
    Task EnrichAsync<T>(IEventSlice<T> slice, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Internal interface for projections that support logger injection.
/// Implemented by <see cref="InlineProjection{T}"/>.
/// </summary>
internal interface ILoggableProjection
{
    void SetLoggerFactory(ILoggerFactory? loggerFactory);
}
