using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
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

    /// <summary>
    /// JSON serialization options for event data deserialization during rebuild.
    /// Uses snake_case lower naming to match SurrealDB conventions.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

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
    /// Projection name for identification in logs and progress tracking.
    /// Defaults to the class name.
    /// </summary>
    public virtual string Name => GetType().Name;

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

    public virtual async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        var events = context.TypedEvents;
        if (events.Count == 0) return;

        var docId = GetDocumentId(events.Select(e => e.Data).ToList().AsReadOnly());
        var tableName = MetadataDispatch.GetTableName(typeof(T));

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

        // Determine action (override for soft-delete, hard-delete, or no-op)
        var action = DetermineAction(aggregate, events);
        if (action == ActionType.Nothing) return;

        // Try to evolve using typed events (new path)
        var result = Evolve(aggregate, events, ct);

        // Fall back to old ApplyEvents if Evolve not overridden (returns same aggregate reference)
        if (ReferenceEquals(result, aggregate) && !IsEvolveOverridden())
            result = ApplyEvents(aggregate, events.Select(e => e.Data).ToList().AsReadOnly(), ct);

        if (action == ActionType.HardDelete)
        {
            if (aggregate is not null) context.Session.Delete(aggregate);
            return;
        }

        if (action == ActionType.SoftDelete && aggregate is ISoftDeleted soft)
        {
            soft.Deleted = true;
            soft.DeletedAt = DateTimeOffset.UtcNow;
            context.Session.Store(aggregate);
            return;
        }

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

    /// <summary>
    /// Determines what action to take after processing events.
    /// Override to control soft-delete, hard-delete, or no-op behavior.
    /// Default is <see cref="ActionType.Store"/>.
    /// </summary>
    protected virtual ActionType DetermineAction(T? aggregate, IReadOnlyList<IEvent> events) => ActionType.Store;

    /// <summary>
    /// Evolve the aggregate using typed events.
    /// Override to process events with full metadata (version, timestamp, etc.).
    /// Default implementation returns <paramref name="aggregate"/> unchanged,
    /// causing the pipeline to fall through to <see cref="ApplyEvents"/>.
    /// </summary>
    protected virtual T? Evolve(T? aggregate, IReadOnlyList<IEvent> events, CancellationToken ct) => aggregate;

    private bool? _evolveOverridden;

    /// <summary>Checks whether the concrete subclass directly overrode <see cref="Evolve"/>.</summary>
    private bool IsEvolveOverridden()
    {
        if (_evolveOverridden.HasValue) return _evolveOverridden.Value;

        var baseMethod = typeof(InlineProjection<T>).GetMethod(nameof(Evolve),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var thisMethod = GetType().GetMethod(nameof(Evolve),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        _evolveOverridden = baseMethod != null && thisMethod != null
            && thisMethod.DeclaringType == GetType();
        return _evolveOverridden.Value;
    }

    /// <summary>
    /// Rebuilds the projected document table by replaying all events from the event store
    /// that match this projection's <see cref="EventTypes"/>. Clears existing data and
    /// re-applies events grouped by stream.
    /// </summary>
    public async Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        var eventTypeNames = EventTypes.Select(t => t.Name).ToList();
        if (eventTypeNames.Count == 0)
        {
            _logger.LogWarning("RebuildAsync called on {ProjectionType} with no EventTypes defined", GetType().Name);
            return;
        }

        // Clear all existing projected documents for this type
        var tableName = MetadataDispatch.GetTableName(typeof(T));
        _logger.LogInformation("Rebuilding projection {ProjectionType} — clearing table {Table}",
            GetType().Name, tableName);
        await session.ExecuteSqlAsync($"DELETE {tableName};", null, ct).ConfigureAwait(false);

        // Query all matching events ordered by stream and version.
        // SurrealQL uses array syntax for IN:  WHERE field IN [val1, val2]
        var typeFilter = string.Join(", ", eventTypeNames.Select(n => $"'{n}'"));
        var sql = $"SELECT * FROM mt_events WHERE event_type IN [{typeFilter}] ORDER BY stream_id ASC, version ASC";

        _logger.LogDebug("Rebuild query: {Sql}", sql);

        // Use the underlying ISurrealDbSession to get the raw SurrealDbResponse,
        // then serialize through JSON to avoid CBOR deserialization limitations.
        if (session is not InternalSessionBase internalSession)
        {
            _logger.LogError("Cannot access underlying session for raw query");
            return;
        }

        var surrealSession = internalSession.Session;
        var response = await surrealSession.RawQuery(sql, null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count == 0)
        {
            _logger.LogInformation("RebuildAsync completed for {ProjectionType} — no events found", GetType().Name);
            return;
        }

        // Use direct CBOR deserialization into EventRow via Column attributes.
        // The SurrealDB CBOR naming convention maps [Column("stream_id")] -> "stream_id",
        // matching the snake_case field names stored in mt_events.
        List<EventRow>? eventRows = null;
        try
        {
            eventRows = response.GetValue<List<EventRow>>(0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize event rows during rebuild");
        }

        if (eventRows is null || eventRows.Count == 0)
        {
            _logger.LogInformation("RebuildAsync completed for {ProjectionType} — no events found", GetType().Name);
            return;
        }

        // Group by stream_id and deserialize events back to typed objects
        var streamGroups = new Dictionary<string, List<object>>();

        foreach (var row in eventRows)
        {
            if (string.IsNullOrEmpty(row.StreamId) || string.IsNullOrEmpty(row.DataJson)) continue;

            var eventType = EventTypes.FirstOrDefault(t => t.Name == row.EventType);
            if (eventType is null) continue;

            try
            {
                var evt = JsonSerializer.Deserialize(row.DataJson, eventType, JsonOptions);
                if (evt is not null)
                {
                    if (!streamGroups.ContainsKey(row.StreamId))
                        streamGroups[row.StreamId] = new List<object>();
                    streamGroups[row.StreamId].Add(evt);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize event {EventType} for stream {StreamId}", row.EventType, row.StreamId);
            }
        }

        // Apply events per stream to rebuild projected documents
        var totalStreams = 0;
        foreach (var (streamId, events) in streamGroups)
        {
            if (events.Count == 0) continue;

            var result = ApplyEvents(null, events.AsReadOnly(), ct);

            if (result is not null)
            {
                var docId = GetDocumentId(events.AsReadOnly());
                if (docId is string id && !string.IsNullOrEmpty(id))
                {
                    result.Id = new RecordIdOf<string>(tableName, id);
                }
                session.Store(result);
                totalStreams++;
            }
        }

        _logger.LogInformation(
            "Rebuilt projection {ProjectionType} from {EventCount} events across {StreamCount} streams",
            GetType().Name, eventRows.Count, totalStreams);
    }

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}

/// <summary>
/// Lightweight DTO for deserializing mt_events rows from the SurrealDB CBOR response.
/// Uses [Column] attributes to match snake_case field names via the SurrealDB CBOR
/// naming convention (which maps ColumnAttribute.Name -> CBOR key).
/// </summary>
internal class EventRow
{
    [Column("stream_id")]
    public string StreamId { get; set; } = "";
    [Column("version")]
    public long Version { get; set; }
    [Column("event_type")]
    public string EventType { get; set; } = "";
    [Column("data_json")]
    public string? DataJson { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
