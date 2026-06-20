using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Base class for projections that run inline during <c>SaveChangesAsync</c>.
/// Subclasses define which event types trigger them and how to build the projected document.
/// </summary>
/// <typeparam name="T">The projected document type (must extend <see cref="Record"/>).</typeparam>
public abstract class InlineProjection<T> : IProjection, ILoggableProjection where T : Record
{
    private ILogger _logger;

    /// <summary>
    /// Logger for this projection. Uses <c>NullLogger{T}</c> by default
    /// unless a logger factory is provided via <c>SetLoggerFactory</c>.
    /// </summary>
    protected ILogger Logger => _logger;

    protected InlineProjection()
    {
        _logger = NullLogger<InlineProjection<T>>.Instance;
    }

    /// <summary>
    /// Injects a logger factory. Called by <see cref="DocumentStore"/> during initialization.
    /// </summary>
    void ILoggableProjection.SetLoggerFactory(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger<InlineProjection<T>>()
            ?? NullLogger<InlineProjection<T>>.Instance;
    }

    public virtual ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;

    /// <summary>
    /// Declares which event types trigger this projection.
    /// </summary>
    public abstract Type[] EventTypes { get; }

    /// <summary>
    /// Given the current aggregate state (or <c>null</c> if new) and a list of new events,
    /// return the new aggregate state, or <c>null</c> to delete the projected document.
    /// </summary>
    protected abstract T? ApplyEvents(T? aggregate, IReadOnlyList<object> events, CancellationToken ct);

    /// <summary>
    /// Determines the document identity from the events (e.g., stream ID).
    /// </summary>
    protected abstract object GetDocumentId(IReadOnlyList<object> events);

    public async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        var events = context.Events;
        if (events.Count == 0) return;

        var docId = GetDocumentId(events);
        var tableName = Snake(typeof(T).Name);

        _logger.LogInformation("Applying inline projection {ProjectionType} for table {Table}",
            GetType().Name, tableName);

        // Try to load existing projected document
        T? aggregate = null;
        try
        {
            if (docId is string id && !string.IsNullOrEmpty(id))
            {
                aggregate = await context.Session.LoadAsync<T>(id, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // First time — document doesn't exist yet
        }

        var result = ApplyEvents(aggregate, events, ct);

        if (result is not null)
        {
            // Preserve existing Id from aggregate, or set from docId for a new document.
            if (aggregate?.Id is not null)
            {
                result.Id = aggregate.Id;
            }
            else if (docId is string s && !string.IsNullOrEmpty(s))
            {
                result.Id = new RecordIdOf<string>(tableName, s);
            }

            context.Session.Store(result);
            _logger.LogDebug("Inline projection stored result for {Type} with id={Id}",
                typeof(T).Name, result.Id);
        }
        else if (aggregate is not null)
        {
            // Projection returned null for an existing document — delete it.
            context.Session.Delete(aggregate);
            _logger.LogDebug("Inline projection deleted existing document for {Type}", typeof(T).Name);
        }
    }

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
