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
    Type[] EventTypes => Type.EmptyTypes;

    /// <summary>
    /// Whether the projection runs inline during SaveChangesAsync or asynchronously.
    /// </summary>
    ProjectionLifecycle Lifecycle { get => ProjectionLifecycle.Inline; set { } }

    /// <summary>
    /// Apply the projection logic to the given events.
    /// </summary>
    Task ApplyAsync(IProjectionContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Projection name for identification in logs and progress tracking.
    /// Defaults to the type name when not explicitly set.
    /// </summary>
    string Name => GetType().Name;

    /// <summary>
    /// Rebuild the projection by replaying ALL events from the event store.
    /// Used for initial projection creation or recovery after data loss.
    /// The projection is responsible for handling idempotency — calling
    /// this twice should produce the same result.
    /// </summary>
    Task RebuildAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Enrichment hook called before <see cref="ApplyAsync(IProjectionContext, CancellationToken)"/>. Projections can pre-load
    /// reference data or the aggregate document. Default is no-op.
    /// </summary>
    Task EnrichAsync<T>(IEventSlice<T> slice, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Marten-compatible ApplyAsync overload. Accepts raw session + event list
    /// and delegates to the <see cref="IProjectionContext"/>-based overload.
    /// </summary>
    Task ApplyAsync(IDocumentOperations operations, IEnumerable<IEvent> events, CancellationToken ct)
        => ApplyAsync(new ProjectionContext(operations, events), ct);

    /// <summary>
    /// Marten-compatible ApplyAsync overload for producers and projections that receive
    /// session stream actions from JasperFx.
    /// </summary>
    Task ApplyAsync(IDocumentOperations operations, IEnumerable<JasperFx.Events.StreamAction> streams, CancellationToken ct)
        => ApplyAsync(
            operations,
            streams.SelectMany(s => s.Events).Select(e => new JasperFxEventAdapter(e)),
            ct);
}

internal sealed class JasperFxEventAdapter : IEvent
{
    private readonly JasperFx.Events.IEvent _inner;

    public JasperFxEventAdapter(JasperFx.Events.IEvent inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public object Data => _inner.Data;
    public long Version => _inner.Version;
    public long Sequence => _inner.Sequence;
    public DateTimeOffset Timestamp => _inner.Timestamp;
    public EventStreamIdentity StreamId => new(_inner.StreamKey ?? _inner.StreamId.ToString(), _inner.StreamId);
    public Guid StreamKey => _inner.StreamId;

    public Dictionary<string, string>? Headers => _inner.Headers?
        .ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty);
}

/// <summary>
/// Internal interface for projections that support logger injection.
/// Implemented by <see cref="InlineProjection{T}"/>.
/// </summary>
internal interface ILoggableProjection
{
    void SetLoggerFactory(ILoggerFactory? loggerFactory);
}
