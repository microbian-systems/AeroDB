using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroDB;

/// <summary>
/// Non-generic "do anything" projection. Subclasses override
/// <see cref="ApplyAsync(IDocumentOperations, IEvent, CancellationToken)"/>
/// to handle each event. Use the <c>operations</c> argument to store or delete
/// any document type.
/// 
/// Equivalent to Marten's non-generic <c>EventProjection</c>.
/// </summary>
public abstract class EventProjection : IProjection, ILoggableProjection
{
    private ILogger _logger = NullLogger.Instance;

    /// <inheritdoc />
    public abstract Type[] EventTypes { get; }

    /// <inheritdoc />
    public virtual ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Inline;

    /// <inheritdoc />
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Override to handle each matching event. Use <paramref name="operations"/>
    /// to Store/Delete/Queue operations.
    /// </summary>
    public virtual Task ApplyAsync(
        IDocumentOperations operations,
        IEvent e,
        CancellationToken cancellation)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        foreach (var e in context.TypedEvents)
        {
            await ApplyAsync(context.Session, e, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public virtual Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        _logger.LogWarning("RebuildAsync called on non-generic EventProjection {Name} — no-op by default", Name);
        return Task.CompletedTask;
    }

    /// <summary>Logger injection hook.</summary>
    void ILoggableProjection.SetLoggerFactory(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }
}
