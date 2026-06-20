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
}
