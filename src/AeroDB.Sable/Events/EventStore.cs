using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AeroDB.Sable.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

public class EventStore : IEvents
{
    private readonly ISurrealDbSession _session;
    private readonly ILogger<EventStore> _logger;
    private readonly StoreOptions? _options;

    public EventStore(ISurrealDbSession session, StoreOptions? options = null)
    {
        _session = session;
        _options = options;
        _logger = options?.LoggerFactory?.CreateLogger<EventStore>()
            ?? NullLogger<EventStore>.Instance;
    }

    public async Task<IReadOnlyList<IEvent>> FetchStream(string streamId, CancellationToken ct = default)
    {
        var response = await _session.RawQuery(
            $"SELECT * FROM mt_events WHERE stream_id = '{streamId}' ORDER BY version ASC;",
            null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 })
                {
                    var upcasters = _options?.Events.Upcasters;
                    return records.Select(r => ToEvent(r, upcasters)).ToList().AsReadOnly();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event deserialization failed for stream {StreamId}", streamId);
                return Array.Empty<IEvent>();
            }
        }

        return Array.Empty<IEvent>();
    }

    public async Task<IReadOnlyList<IEvent>> Append(string streamId, IEnumerable<object> events, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var isQuick = _options?.Events.AppendMode == EventAppendMode.Quick;
        var dataMasking = _options?.Events.DataMaskingPredicate;

        var version = await GetNextVersion(streamId, ct).ConfigureAwait(false);
        long sequence = 0;
        Guid streamKey = Guid.Empty;
        if (!isQuick)
        {
            sequence = await GetNextSequence(ct).ConfigureAwait(false);
            streamKey = await GetOrCreateStreamKey(streamId, ct).ConfigureAwait(false);
        }
        var wrapped = new List<IEvent>();

        var serializationMode = _options?.Events.SerializationMode ?? EventSerializationMode.Json;
        var streamKeyValue = isQuick ? "" : streamKey.ToString();
        var streamKeyGuid = isQuick ? Guid.Empty : streamKey;

        foreach (var evt in events)
        {
            version++;
            if (!isQuick) sequence++;

            // Serialize headers if provided
            string? headersJson = null;
            if (headers is { Count: > 0 })
                headersJson = System.Text.Json.JsonSerializer.Serialize(headers, JsonOptions);

            // Check data masking predicate BEFORE serialization (GDPR compliance)
            bool isMasked = false;
            if (dataMasking is not null)
            {
                var tempEvent = (IEvent)Activator.CreateInstance(
                    typeof(Event<>).MakeGenericType(evt.GetType()),
                    [evt, version, isQuick ? 0 : sequence, DateTimeOffset.UtcNow, streamId, streamKeyGuid, null])!;
                isMasked = dataMasking(tempEvent);
            }

            var record = new EventRecord
            {
                StreamId = streamId,
                Version = version,
                Sequence = isQuick ? 0 : sequence,
                StreamKey = streamKeyValue,
                EventType = evt.GetType().Name,
                CreatedAt = DateTime.UtcNow,
                HeadersJson = headersJson ?? ""
            };

            if (isMasked)
            {
                // GDPR/redaction: store event metadata only, null out the event data
                record.DataJson = null;
                record.DataBinary = null;
                _logger.LogInformation("Data masked for event {EventType} in stream {StreamId}",
                    evt.GetType().Name, streamId);
            }
            else if (serializationMode == EventSerializationMode.Binary)
            {
                record.DataBinary = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOptions);
                record.DataJson = null;
            }
            else
            {
                record.DataJson = JsonSerializer.Serialize(evt, JsonOptions);
                record.DataBinary = null;
            }

            _logger.LogDebug("Appending event {EventType} to stream {StreamId} (version {Version}, seq {Sequence})",
                evt.GetType().Name, streamId, version, sequence);

            // Use the SDK's typed Create method with CBOR serialization instead of raw SurrealQL.
            // The [Column] attributes on EventRecord ensure CBOR uses snake_case field names
            // matching the mt_events schema.
            await _session.Create("mt_events", record, ct).ConfigureAwait(false);

            wrapped.Add(WrapEvent(evt, record));
        }

        return wrapped.AsReadOnly();
    }

    public async Task<IReadOnlyList<IEvent>> Append(string streamId, long expectedVersion, IEnumerable<object> events, CancellationToken ct = default)
    {
        await using var tx = await _session.BeginTransaction(ct).ConfigureAwait(false);
        try
        {
            var currentVersion = await GetNextVersion(streamId, ct).ConfigureAwait(false);
            if (currentVersion != expectedVersion)
                throw new ConcurrencyException(typeof(EventStore), streamId, expectedVersion, currentVersion);

            var result = await Append(streamId, events, headers: null, ct).ConfigureAwait(false);
            await tx.Commit(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.Cancel(ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task<IReadOnlyList<IEvent>> AppendOptimistic(string streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        => Append(streamId, lastKnownVersion, events, ct);

    public Task<IReadOnlyList<IEvent>> AppendExclusive(string streamId, IEnumerable<object> events, CancellationToken ct = default)
        => Append(streamId, 0, events, ct);

    public Task<IReadOnlyList<IEvent>> AppendOptimistic(Guid streamId, long lastKnownVersion, IEnumerable<object> events, CancellationToken ct = default)
        => Append(streamId.ToString("D"), lastKnownVersion, events, ct);

    public Task<IReadOnlyList<IEvent>> AppendExclusive(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
        => Append(streamId.ToString("D"), 0, events, ct);

    public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        await Append(streamId, events, headers: null, ct).ConfigureAwait(false);
        return streamId;
    }

    public Task<string> StartStream<T>(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        // Store the stream type on the first event or as stream metadata
        // For now, delegates to the base StartStream
        return StartStream(streamId, events, ct);
    }

    public Task<string> StartStream<T>(Guid streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        return StartStream<T>(streamId.ToString("D"), events, ct);
    }

    public async Task ArchiveStream(string streamId, CancellationToken ct = default)
    {
        await _session.RawQuery(
            $"CREATE mt_archived_streams CONTENT {{ stream_id: '{streamId}', archived_at: time::now() }};",
            null, ct).ConfigureAwait(false);
        _logger.LogInformation("Archived stream {StreamId}", streamId);
    }

    public Task ArchiveStream(Guid streamId, CancellationToken ct = default)
        => ArchiveStream(streamId.ToString("D"), ct);

    public async Task<IReadOnlyList<IEvent>> WriteTombstone(string streamId, long version, CancellationToken ct = default)
    {
        var tombstoneEvent = new TombstoneEvent { StreamId = streamId, Version = version, Reason = "gap-fill" };
        return await Append(streamId, new[] { tombstoneEvent }, headers: null, ct).ConfigureAwait(false);
    }

    public async Task<FetchForWritingResult<T>> FetchForWritingAsync<T>(string streamId, CancellationToken ct = default) where T : class
    {
        var events = await FetchStream(streamId, ct).ConfigureAwait(false);

        T? aggregate = null;
        long expectedVersion = 0;

        if (events.Count > 0)
        {
            // Use LiveStreamAggregation to build the aggregate from events
            aggregate = LiveStreamAggregation.AggregateEvents<T>(events);
            expectedVersion = events[^1].Version;
        }

        return new FetchForWritingResult<T>(aggregate, expectedVersion, streamId);
    }

    public async Task<T?> AggregateStreamAsync<T>(
        string streamId,
        long? version = null,
        DateTimeOffset? timestamp = null,
        T? state = default,
        long? fromVersion = null,
        CancellationToken ct = default) where T : class
    {
        IReadOnlyList<IEvent> events;

        // Use the faster FetchStream path when no filters are specified
        if (version is null && timestamp is null && fromVersion is null)
        {
            events = await FetchStream(streamId, ct).ConfigureAwait(false);
        }
        else
        {
            events = await FetchStreamAsync(streamId, version, timestamp, fromVersion, ct).ConfigureAwait(false);
        }
        if (events.Count == 0)
            return state ?? default;

        T aggregate;
        if (state is not null)
        {
            aggregate = state;
            foreach (var e in events)
            {
                if (e.Data is null) continue;
                var eventType = e.Data.GetType();
                var method = typeof(T).GetMethod("Apply", [eventType])
                    ?? typeof(T).GetMethod("When", [eventType]);
                method?.Invoke(aggregate, [e.Data]);
            }
        }
        else
        {
            aggregate = LiveStreamAggregation.AggregateEvents<T>(events);
        }

        return aggregate;
    }

    public async Task<StreamState?> FetchStreamStateAsync(string streamId, CancellationToken ct = default)
    {
        // Query aggregate event metadata
        var response = await _session.RawQuery(
            $"SELECT count() AS total_events, " +
            $"math::min(created_at) AS first_event, " +
            $"math::max(created_at) AS last_event " +
            $"FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}' " +
            $"GROUP ALL;",
            null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count == 0)
            return null;

        long version = 0;
        DateTimeOffset? created = null;
        DateTimeOffset? lastModified = null;

        try
        {
            var result = response.GetValue<List<Dictionary<string, object>>>(0);
            if (result is { Count: > 0 })
            {
                var row = result[0];

                if (row.TryGetValue("total_events", out var totalVal) && totalVal is not null)
                    version = Convert.ToInt64(totalVal);

                if (row.TryGetValue("first_event", out var firstVal) && firstVal is not null)
                {
                    created = firstVal switch
                    {
                        DateTimeOffset dto => dto,
                        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
                        string s => DateTimeOffset.TryParse(s, out var parsed) ? parsed : null,
                        _ => null
                    };
                }

                if (row.TryGetValue("last_event", out var lastVal) && lastVal is not null)
                {
                    lastModified = lastVal switch
                    {
                        DateTimeOffset dto => dto,
                        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
                        string s => DateTimeOffset.TryParse(s, out var parsed) ? parsed : null,
                        _ => null
                    };
                }
            }
        }
        catch
        {
            // CBOR deserialization fallback
        }

        // Check archive status
        bool isArchived = false;
        try
        {
            var archiveResponse = await _session.RawQuery(
                $"SELECT * FROM mt_archived_streams WHERE stream_id = '{streamId.Replace("'", "\\'")}' LIMIT 1;",
                null, ct).ConfigureAwait(false);
            isArchived = !archiveResponse.HasErrors && archiveResponse.Count > 0;
        }
        catch
        {
            // Table may not exist
        }

        return new StreamState
        {
            StreamId = streamId,
            Version = version,
            Created = created,
            LastModified = lastModified,
            IsArchived = isArchived
        };
    }

    public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken ct = default)
        => FetchStreamStateAsync(streamId.ToString("D"), ct);

    public async Task<IReadOnlyList<IEvent>> FetchStreamAsync(
        string streamId,
        long? version = null,
        DateTimeOffset? timestamp = null,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        var conditions = new List<string> { $"stream_id = '{streamId.Replace("'", "\\'")}'" };

        if (version.HasValue)
            conditions.Add($"version = {version.Value}");

        if (timestamp.HasValue)
        {
            var tsStr = timestamp.Value.ToString("O");
            conditions.Add($"created_at >= d'{tsStr}'");
        }

        if (fromVersion.HasValue)
            conditions.Add($"version >= {fromVersion.Value}");

        var whereClause = string.Join(" AND ", conditions);
        var sql = $"SELECT * FROM mt_events WHERE {whereClause} ORDER BY version ASC;";

        var response = await _session.RawQuery(sql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 })
                {
                    var upcasters = _options?.Events.Upcasters;
                    return records.Select(r => ToEvent(r, upcasters)).ToList().AsReadOnly();
                }
            }
            catch
            {
                // CBOR deserialization fallback
            }
        }

        return [];
    }

    public async Task<IReadOnlyList<IEvent>> FetchAllAfterSequence(
        long sequence, CancellationToken ct = default)
    {
        var response = await _session.RawQuery(
            $"SELECT * FROM mt_events WHERE sequence > {sequence} ORDER BY sequence ASC;",
            null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 })
                {
                    var upcasters = _options?.Events.Upcasters;
                    var results = records.Select(r => ToEvent(r, upcasters)).ToList();
                    _logger.LogDebug("Fetched {Count} events after sequence {Sequence}", results.Count, sequence);
                    return results.AsReadOnly();
                }
            }
            catch
            {
                // CBOR deserialization fallback — return empty, daemon will retry
            }
        }

        return [];
    }

    private async Task<long> GetNextVersion(string streamId, CancellationToken ct)
    {
        // Query the latest version for this stream.
        // Avoid math::max aggregate in CBOR mode (can cause deserialization issues).
        var response = await _session.RawQuery(
            $"SELECT version FROM mt_events WHERE stream_id = '{streamId}' ORDER BY version DESC LIMIT 1;",
            null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 })
                    return records[0].Version;
            }
            catch
            {
                // CBOR deserialization fallback
            }
        }

        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Converts an <see cref="EventRecord"/> to an <see cref="IEvent"/> by deserializing the data JSON
    /// to the correct type stored in <see cref="EventRecord.EventType"/>.
    /// If upcasters are provided, old event types are migrated to new types.
    /// </summary>
    private static IEvent ToEvent(EventRecord r, List<IEventUpcaster>? upcasters = null)
    {
        // Resolve the concrete event type from the stored name
        object? data;
        var eventTypeName = r.EventType;

        // Determine if we have binary or JSON data
        var hasBinaryData = r.DataBinary is { Length: > 0 };
        var hasJsonData = !string.IsNullOrEmpty(r.DataJson);

        if (!string.IsNullOrEmpty(eventTypeName) && (hasBinaryData || hasJsonData))
        {
            Type? type = null;
            try { type = Type.GetType(eventTypeName, throwOnError: false); }
            catch { }

            // Backward compat: old events with simple type names only
            if (type == null && !eventTypeName.Contains('.'))
            {
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.Name == eventTypeName);
            }

            if (hasBinaryData)
            {
                if (type != null)
                {
                    try { data = JsonSerializer.Deserialize(r.DataBinary, type, JsonOptions); }
                    catch { data = r.DataBinary; }
                }
                else
                {
                    try { data = JsonSerializer.Deserialize<object>(r.DataBinary, JsonOptions) ?? (object?)r.DataBinary; }
                    catch { data = r.DataBinary; }
                }
            }
            else
            {
                if (type != null)
                {
                    try { data = JsonSerializer.Deserialize(r.DataJson!, type, JsonOptions); }
                    catch { data = r.DataJson; }
                }
                else
                {
                    try { data = JsonSerializer.Deserialize<object>(r.DataJson!, JsonOptions) ?? r.DataJson; }
                    catch { data = r.DataJson; }
                }
            }

            // Check for upcasters that can migrate old event types to new types
            if (upcasters is { Count: > 0 } && data is not null)
            {
                foreach (var upcaster in upcasters)
                {
                    if (eventTypeName.Equals(upcaster.OldEventType, StringComparison.OrdinalIgnoreCase))
                    {
                        data = upcaster.Upcast(data);
                        eventTypeName = data.GetType().Name;
                        break;
                    }
                }
            }
        }
        else
        {
            data = r.DataJson ?? (object?)r.DataBinary;
        }

        var streamKey = string.IsNullOrEmpty(r.StreamKey)
            ? Guid.Empty
            : Guid.TryParse(r.StreamKey, out var g) ? g : Guid.Empty;

        // Deserialize headers if present
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(r.HeadersJson))
        {
            try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(r.HeadersJson, JsonOptions); }
            catch { /* ignore malformed headers */ }
        }

        return new Event<object>(
            data ?? "",
            r.Version,
            r.Sequence,
            ToDateTimeOffset(r.CreatedAt),
            r.StreamId,
            streamKey,
            headers);
    }

    public async Task<int> BulkInsertEventsAsync(
        IEnumerable<(string StreamId, IEnumerable<object> Events)> streams,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        int totalInserted = 0;

        // Track version per stream across all chunks so that when a single stream's
        // events span multiple batches, each event still gets a unique (stream_id, version)
        // pair.  This is necessary because mt_events may have a UNIQUE INDEX on
        // (stream_id, version) set up by EnsureEventSchemaAsync, and a SCHEMAFULL table
        // that requires the version field.
        var versionByStream = new Dictionary<string, long>();

        foreach (var chunk in streams.Chunk(batchSize))
        {
            var statements = new List<string>();

            foreach (var (streamId, events) in chunk)
            {
                if (!versionByStream.TryGetValue(streamId, out var version))
                    version = 0;

                foreach (var evt in events)
                {
                    version++;
                    var json = JsonSerializer.Serialize(evt, jsonOptions);
                    var eventType = evt.GetType().Name;
                    var streamIdEscaped = streamId.Replace("'", "\\'");
                    var escapedJson = json.Replace("'", "\\'");
                    statements.Add(
                        $"INSERT INTO mt_events {{ stream_id: '{streamIdEscaped}', version: {version}, sequence: 0, stream_key: '', event_type: '{eventType}', data_json: '{escapedJson}', headers_json: NONE, created_at: time::now() }}");
                }

                versionByStream[streamId] = version;
            }

            if (statements.Count > 0)
            {
                var surql = string.Join("; ", statements) + ";";
                var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);
                if (!response.HasErrors)
                    totalInserted += statements.Count;
            }
        }

        return totalInserted;
    }

    // ───────────────────────────────────────────────────────────────────
    // Marten parity items
    // ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<T?> AggregateStreamToLastKnownAsync<T>(string streamId, CancellationToken ct = default) where T : class
    {
        // Try to load the last known projected state from the projection table.
        // The projected document uses the stream ID as its record ID.
        T? state = default;
        long lastKnownVersion = 0;

        try
        {
            // Load the projected aggregate (single-stream projection stores doc with streamId as key)
            state = await _session.Select<T>(new RecordIdOf<string>(
                MetadataDispatch.GetTableName(typeof(T)), streamId), ct).ConfigureAwait(false);

            if (state is not null)
            {
                // Determine the last applied version from the state.
                // Convention: look for a Version property, or record the event count from the stream.
                var versionProp = typeof(T).GetProperty("Version",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (versionProp is not null && versionProp.PropertyType == typeof(long))
                {
                    lastKnownVersion = (long)versionProp.GetValue(state)!;
                }
            }
        }
        catch
        {
            // Projection table may not exist or type is not a projection — fall through to full aggregation
        }

        // Fetch events after the last known version and apply them
        if (lastKnownVersion > 0)
        {
            var newEvents = await FetchStreamAsync(streamId, fromVersion: lastKnownVersion + 1, ct: ct).ConfigureAwait(false);
            if (newEvents.Count == 0)
                return state;

            foreach (var e in newEvents)
            {
                if (e.Data is null) continue;
                var eventType = e.Data.GetType();
                var method = typeof(T).GetMethod("Apply", [eventType])
                    ?? typeof(T).GetMethod("When", [eventType]);
                method?.Invoke(state, [e.Data]);
            }
            return state;
        }

        // Fall back to full stream aggregation
        return await AggregateStreamAsync<T>(streamId, ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CompactStreamAsync<T>(string streamId, Action<CompactStreamOptions>? configure = null, CancellationToken ct = default) where T : class
    {
        var options = new CompactStreamOptions();
        configure?.Invoke(options);

        // Aggregate the full stream to get the current state
        var events = await FetchStream(streamId, ct).ConfigureAwait(false);
        if (events.Count == 0) return;

        var aggregate = LiveStreamAggregation.AggregateEvents<T>(events);

        // If KeepRecentEvents is set, determine how many events to keep
        int eventsToDelete;
        if (options.KeepRecentEvents.HasValue && options.KeepRecentEvents.Value > 0)
        {
            eventsToDelete = Math.Max(0, events.Count - options.KeepRecentEvents.Value);
            if (eventsToDelete == 0) return; // Nothing to compact
        }
        else
        {
            eventsToDelete = events.Count;
        }

        // Optionally archive the events being deleted
        if (options.KeepArchivedCopy && eventsToDelete > 0)
        {
            var eventsToArchive = events.Take(eventsToDelete);
            foreach (var evt in eventsToArchive)
            {
                await _session.RawQuery(
                    $"CREATE mt_archived_events CONTENT {{ stream_id: '{streamId.Replace("'", "\\'")}', " +
                    $"version: {evt.Version}, event_type: '{evt.Data?.GetType().Name ?? "unknown"}', " +
                    $"archived_at: time::now() }};",
                    null, ct).ConfigureAwait(false);
            }
        }

        // Delete old events
        var deleteSql = options.KeepRecentEvents.HasValue
            ? $"DELETE FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}' " +
              $"AND version <= {events.Count - options.KeepRecentEvents.Value};"
            : $"DELETE FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}';";
        await _session.RawQuery(deleteSql, null, ct).ConfigureAwait(false);

        // Insert the snapshot event
        var snapshotRecord = new EventRecord
        {
            StreamId = streamId,
            Version = events.Count, // Use the last version number
            Sequence = 0,
            StreamKey = "",
            EventType = aggregate?.GetType().Name ?? typeof(T).Name,
            CreatedAt = DateTime.UtcNow,
            DataJson = aggregate is not null ? JsonSerializer.Serialize(aggregate, JsonOptions) : null,
            DataBinary = null
        };
        await _session.Create("mt_events", snapshotRecord, ct).ConfigureAwait(false);

        _logger.LogInformation("Compacted stream {StreamId}: {DeletedCount} events replaced with snapshot of type {SnapshotType}",
            streamId, eventsToDelete, snapshotRecord.EventType);
    }

    /// <inheritdoc />
    public async Task<FetchForWritingResult<T>?> FetchForExclusiveWriting<T>(string streamId, CancellationToken ct = default) where T : class
    {
        // Use a SurrealDB BEGIN TRANSACTION to get exclusive access
        // Check if the stream exists
        var streamState = await FetchStreamStateAsync(streamId, ct).ConfigureAwait(false);

        // If stream has no events, we can write exclusively — no concurrency risk
        if (streamState is null || streamState.Version == 0)
        {
            return new FetchForWritingResult<T>(null, 0, streamId);
        }

        // Lock the stream by reading the latest version inside a transaction.
        // In SurrealDB, transactions serialize writes.
        var tx = await _session.BeginTransaction(ct).ConfigureAwait(false);
        try
        {
            var checkResponse = await _session.RawQuery(
                $"SELECT version FROM mt_events WHERE stream_id = '{streamId.Replace("'", "\\'")}' " +
                $"ORDER BY version DESC LIMIT 1;",
                null, ct).ConfigureAwait(false);

            long currentVersion = 0;
            if (!checkResponse.HasErrors && checkResponse.Count > 0)
            {
                try
                {
                    var records = checkResponse.GetValue<List<EventRecord>>(0);
                    if (records is { Count: > 0 })
                        currentVersion = records[0].Version;
                }
                catch { }
            }

            // Fetch events and aggregate
            var events = await FetchStream(streamId, ct).ConfigureAwait(false);
            T? aggregate = null;
            if (events.Count > 0)
            {
                aggregate = LiveStreamAggregation.AggregateEvents<T>(events);
            }

            // Keep the transaction open — the caller uses this result and
            // SaveChangesAsync will commit the transaction.
            // Note: the caller's SaveChangesAsync must append events via the
            // returned FetchForWritingResult<T>.
            var result = new FetchForWritingResult<T>(aggregate, currentVersion, streamId);

            // Commit the lock-read transaction immediately (it was just a read-lock).
            // The actual write will happen in SaveChangesAsync with its own concurrency check.
            await tx.Commit(ct).ConfigureAwait(false);

            return result;
        }
        catch
        {
            await tx.Cancel(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ISurrealDbQueryable<T> QueryRawEventDataOnly<T>() where T : class
    {
        var provider = new SurrealQueryProvider(_session, _options!);
        var queryable = new SurrealDbQueryable<T>(provider);
        // Scope queries to the mt_events table
        queryable.ViewName = "mt_events";
        return queryable;
    }

    /// <inheritdoc />
    public ISurrealDbQueryable<IEvent> QueryAllRawEvents()
    {
        var provider = new SurrealQueryProvider(_session, _options!);
        var queryable = new SurrealDbQueryable<IEvent>(provider);
        // Scope queries to the mt_events table
        queryable.ViewName = "mt_events";
        return queryable;
    }

    /// <inheritdoc />
    public IEvent BuildEvent(object data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return (IEvent)Activator.CreateInstance(
            typeof(Event<>).MakeGenericType(data.GetType()),
            [data, 0L, 0L, DateTimeOffset.UtcNow, "", Guid.Empty, null])!;
    }

    /// <inheritdoc />
    public async Task OverwriteEventAsync(IEvent e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(e.Data, JsonOptions);
        var sql = "UPDATE mt_events SET data_json = $data, data_binary = NONE WHERE sequence = $seq AND stream_id = $sid;";
        var parameters = new Dictionary<string, object?>
        {
            ["data"] = dataJson,
            ["seq"] = e.Sequence,
            ["sid"] = e.StreamId.Value // IMPORTANT! Use Value not StreamId obj to avoid issues w/ CBOR serialization
        };
        await _session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        _logger.LogDebug("Overwrote data for event seq={Sequence} in stream {StreamId}", e.Sequence, e.StreamId);
    }

    /// <inheritdoc />
    public async Task DeleteSingleEventAsync(string streamId, long eventSequence, CancellationToken ct = default)
    {
        // Soft-delete: clear the data payload to preserve event metadata
        // (version, sequence, timestamp, stream identity) for stream integrity.
        var sql = "UPDATE mt_events SET data_json = NONE, data_binary = NONE WHERE sequence = $seq AND stream_id = $sid;";
        var parameters = new Dictionary<string, object?>
        {
            ["seq"] = eventSequence,
            ["sid"] = streamId
        };
        await _session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        _logger.LogDebug("Soft-deleted event seq={Sequence} in stream {StreamId}", eventSequence, streamId);
    }

    /// <summary>
    /// Wraps a raw event object and its stored <see cref="EventRecord"/> into an <see cref="IEvent"/>.
    /// </summary>
    private static IEvent WrapEvent(object evt, EventRecord record)
    {
        var streamKey = string.IsNullOrEmpty(record.StreamKey)
            ? Guid.Empty
            : Guid.TryParse(record.StreamKey, out var g) ? g : Guid.Empty;

        // Deserialize headers if present
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(record.HeadersJson))
        {
            try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(record.HeadersJson, JsonOptions); }
            catch { /* ignore malformed headers */ }
        }

        return (IEvent)Activator.CreateInstance(
            typeof(Event<>).MakeGenericType(evt.GetType()),
            [evt, record.Version, record.Sequence, ToDateTimeOffset(record.CreatedAt), record.StreamId, streamKey, headers])!;
    }

    private async Task<long> GetNextSequence(CancellationToken ct)
    {
        // Sequence is used for ordering, not uniqueness. Minor duplicates under
        // extreme concurrent writes are acceptable (TOCTOU race between count
        // read and the subsequent INSERT is non-critical).
        try
        {
            var response = await _session.RawQuery(
                "SELECT count() FROM mt_events GROUP ALL;", null, ct).ConfigureAwait(false);
            if (!response.HasErrors && response.Count > 0)
            {
                // count() returns [{ count: N }] — extract from the dictionary
                var result = response.GetValue<List<Dictionary<string, object>>>(0);
                if (result is { Count: > 0 } && result[0].TryGetValue("count", out var countVal))
                    return Convert.ToInt64(countVal) + 1;
            }
        }
        catch
        {
            // Ignore and fall through to ticks fallback
        }
        // Fallback: use ticks for ordering (not guaranteed unique)
        return DateTimeOffset.UtcNow.Ticks;
    }

    private async Task<Guid> GetOrCreateStreamKey(string streamId, CancellationToken ct)
    {
        // Try to get existing stream key
        var response = await _session.RawQuery(
            $"SELECT stream_key FROM mt_events WHERE stream_id = '{streamId}' LIMIT 1;",
            null, ct).ConfigureAwait(false);
        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 } && !string.IsNullOrEmpty(records[0].StreamKey))
                    return Guid.Parse(records[0].StreamKey);
            }
            catch
            {
                // Fallback
            }
        }
        return Guid.NewGuid();
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        return value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : new DateTimeOffset(value.ToUniversalTime());
    }

    /// <inheritdoc />
    public async Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Build WHERE clause: tags CONTAINS each value
        var conditions = query.TagValues.Select(t => $"tags CONTAINS '{t.Replace("'", "\\'")}'");
        var whereClause = string.Join(" OR ", conditions);

        if (query.EventType is not null)
        {
            var escapedType = query.EventType.Replace("'", "\\'");
            whereClause = $"({whereClause}) AND event_type = '{escapedType}'";
        }

        var surql = $"SELECT count() FROM mt_events WHERE {whereClause} GROUP ALL;";

        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count == 0)
            return false;

        try
        {
            var result = response.GetValue<List<Dictionary<string, object>>>(0);
            if (result is { Count: > 0 } && result[0].TryGetValue("count", out var countVal) && countVal is not null)
                return Convert.ToInt64(countVal) > 0;
        }
        catch
        {
            // CBOR deserialization fallback
        }

        return false;
    }
}

internal class EventRecord
{
    [Column("stream_id")]
    public string StreamId { get; set; } = "";
    [Column("version")]
    public long Version { get; set; }
    [Column("sequence")]
    public long Sequence { get; set; }
    [Column("stream_key")]
    public string StreamKey { get; set; } = "";
    [Column("event_type")]
    public string EventType { get; set; } = "";
    [Column("data_json")]
    public string? DataJson { get; set; }
    [Column("data_binary")]
    public byte[]? DataBinary { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("headers_json")]
    public string HeadersJson { get; set; } = "";
}
