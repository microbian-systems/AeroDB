using Microsoft.Extensions.Logging;

namespace Dali;

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
    /// Rebuild the projection by replaying ALL events from the event store.
    /// Used for initial projection creation or recovery after data loss.
    /// The projection is responsible for handling idempotency — calling
    /// this twice should produce the same result.
    /// </summary>
    Task RebuildAsync(IDocumentSession session, CancellationToken ct);
}

/// <summary>
/// Internal interface for projections that support logger injection.
/// Implemented by <see cref="InlineProjection{T}"/>.
/// </summary>
internal interface ILoggableProjection
{
    void SetLoggerFactory(ILoggerFactory? loggerFactory);
}
