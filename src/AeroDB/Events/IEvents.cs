namespace AeroDB;

/// <summary>The event sourcing API surface. Provides methods for appending events, starting streams, fetching streams, applying optimistic concurrency, archiving streams, and writing tombstone events.</summary>
public interface IEvents
{
    /// <summary>
    /// Fetches all events for a stream, wrapped in <see cref="IEvent"/> envelopes
    /// with full metadata (version, sequence, timestamp, stream identity).
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchStream(string streamId, CancellationToken ct = default);

    /// <summary>
    /// Appends events to a stream and returns the wrapped <see cref="IEvent"/> envelopes
    /// with assigned version, sequence, and stream key metadata.
    /// Optional <paramref name="headers"/> are persisted as event metadata for tracing causality chains.
    /// </summary>
    Task<IReadOnlyList<IEvent>> Append(string streamId, IEnumerable<object> events, Dictionary<string, string>? headers = null, CancellationToken ct = default);

    /// <summary>
    /// Appends events to a stream with optimistic concurrency. Throws <see cref="ConcurrencyException"/>
    /// if the stream's current version does not match <paramref name="expectedVersion"/>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Append events with optimistic concurrency. The expected version is auto-derived
    /// from the latest version in the stream at the time events were loaded.
    /// Equivalent to <c>Append(streamId, lastKnownVersion, events)</c>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Append events with exclusive locking. Ensures no events exist in the stream yet.
    /// Equivalent to <c>Append(streamId, 0, events)</c>.
    /// </summary>
    Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant of <c>AppendOptimistic</c>.</summary>
    Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant of <c>AppendExclusive</c>.</summary>
    Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Starts a new stream by appending the initial events.
    /// Returns the stream ID on success.
    /// </summary>
    Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Marten-compatible single-event start stream overload.</summary>
    Task<string> StartStream<T>(Guid streamId, object @event, CancellationToken ct = default)
        => StartStream<T>(streamId, new[] { @event }, ct);

    /// <summary>Start a new stream with typed stream identity.</summary>
    Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>Guid variant with typed stream identity.</summary>
    Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Marten-compatible aggregate write workflow.
    /// </summary>
    async Task WriteToAggregate<T>(
        Guid streamId,
        int version,
        Action<FetchForWritingResult<T>> handler,
        CancellationToken ct = default)
        where T : class
    {
        var stream = await FetchForWritingAsync<T>(streamId.ToString(), ct).ConfigureAwait(false);
        handler(stream);
    }

    /// <summary>
    /// Fetches all events after a given global sequence number (for async projections / polling).
    /// Uses the global <see cref="IEvent.Sequence"/> (not per-stream version) for ordering
    /// to avoid skipping low-volume streams.
    /// Returns events wrapped in <see cref="IEvent"/> envelopes.
    /// </summary>
    Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
        long sequence, CancellationToken ct = default);

    /// <summary>
    /// Archive a stream — moves events to the archive table.
    /// After archiving, the stream cannot receive new events.
    /// </summary>
    Task ArchiveStream(string streamId, CancellationToken ct = default);

    /// <summary>Guid variant.</summary>
    Task ArchiveStream(Guid streamId, CancellationToken ct = default);

    /// <summary>
    /// Fetches an aggregate for write-model operations. The returned result tracks
    /// the expected stream version; SaveChangesAsync will throw ConcurrencyException
    /// if another process has appended to the stream since this fetch.
    /// </summary>
    Task<FetchForWritingResult<T>> FetchForWritingAsync<T>(string streamId, CancellationToken ct = default) where T : class;

    /// <summary>Fetch stream state metadata without loading events.</summary>
    Task<StreamState?> FetchStreamStateAsync(string streamId, CancellationToken ct = default);

    /// <summary>Guid variant of <see cref="FetchStreamStateAsync(string, CancellationToken)"/>.</summary>
    Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken ct = default);

    /// <summary>Fetch events from a stream with optional version/timestamp filtering.</summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="version">Exact version filter (optional).</param>
    /// <param name="timestamp">Only include events at or after this timestamp (optional).</param>
    /// <param name="fromVersion">Only include events at or after this version (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<IEvent>> FetchStreamAsync(
        string streamId,
        long? version = null,
        DateTimeOffset? timestamp = null,
        long? fromVersion = null,
        CancellationToken ct = default);

    /// <summary>Fetches and aggregates a stream into type T. Returns default(T) if stream is empty.
    /// When <paramref name="state"/> is provided, events are applied to it instead of creating a new instance.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="version">Exact version filter (optional).</param>
    /// <param name="timestamp">Only include events at or after this timestamp (optional).</param>
    /// <param name="state">Optional pre-existing aggregate state to apply events onto.</param>
    /// <param name="fromVersion">Only include events at or after this version (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<T?> AggregateStreamAsync<T>(
        string streamId,
        long? version = null,
        DateTimeOffset? timestamp = null,
        T? state = default,
        long? fromVersion = null,
        CancellationToken ct = default) where T : class;

    /// <summary>
    /// Write a tombstone event to fill a gap caused by a failed transaction.
    /// Use this for idempotent retry scenarios.
    /// </summary>
    Task<IReadOnlyList<IEvent>> WriteTombstone(string streamId, long version, CancellationToken ct = default);

    /// <summary>
    /// Bulk-inserts events across multiple streams in batches for performance.
    /// All events in a batch are inserted atomically via a single SurrealQL statement.
    /// </summary>
    Task<int> BulkInsertEventsAsync(
        IEnumerable<(string StreamId, IEnumerable<object> Events)> streams,
        int batchSize = 100,
        CancellationToken ct = default);

    /// <summary>Aggregate a stream using the last known state from the projection table.
    /// Falls back to full stream aggregation if no cached state exists.</summary>
    Task<T?> AggregateStreamToLastKnownAsync<T>(string streamId, CancellationToken ct = default) where T : class;

    /// <summary>Compact an event stream: replaces all events with a single snapshot event
    /// representing the current aggregate state, then deletes old events.</summary>
    Task CompactStreamAsync<T>(string streamId, Action<CompactStreamOptions>? configure = null, CancellationToken ct = default) where T : class;

    /// <summary>Fetch stream for writing with an exclusive lock (prevents concurrent appends).</summary>
    Task<FetchForWritingResult<T>?> FetchForExclusiveWriting<T>(string streamId, CancellationToken ct = default) where T : class;

    /// <summary>Query raw event data of a specific type across all streams.</summary>
    ISurrealDbQueryable<T> QueryRawEventDataOnly<T>() where T : class;

    /// <summary>Query all raw events across all streams.</summary>
    ISurrealDbQueryable<IEvent> QueryAllRawEvents();

    /// <summary>
    /// Build an <see cref="IEvent"/> envelope from raw event data without persisting it.
    /// The returned event carries the current timestamp but no version/sequence/stream identity.
    /// </summary>
    /// <param name="data">The raw event data object.</param>
    IEvent BuildEvent(object data);

    /// <summary>
    /// Overwrite an existing event's data. Updates the event in the database,
    /// replacing the stored data with the new serialized payload.
    /// </summary>
    /// <param name="e">The event envelope containing the new data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OverwriteEventAsync(IEvent e, CancellationToken ct = default);

    /// <summary>
    /// Soft-delete a single event by clearing its data payload.
    /// Preserves event metadata (version, sequence, timestamp) for stream integrity.
    /// Does NOT physically remove the event record.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="eventSequence">The global sequence number of the event to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteSingleEventAsync(string streamId, long eventSequence, CancellationToken ct = default);

    /// <summary>
    /// Check if any events exist matching the given tag query.
    /// Used by the async daemon to determine if there are events to project for DCB tags.
    /// </summary>
    Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken ct = default);
}
