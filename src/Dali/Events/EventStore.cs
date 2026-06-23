using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class EventStore : IEvents
{
    private readonly ISurrealDbSession _session;
    private readonly ILogger<EventStore> _logger;

    public EventStore(ISurrealDbSession session, StoreOptions? options = null)
    {
        _session = session;
        _logger = options?.LoggerFactory?.CreateLogger<EventStore>()
            ?? NullLogger<EventStore>.Instance;
    }

    public async Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default)
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
                    return records.Select(r =>
                    {
                        if (!string.IsNullOrEmpty(r.DataJson))
                        {
                            try { return JsonSerializer.Deserialize<object>(r.DataJson, JsonOptions) ?? r.DataJson; }
                            catch { return r.DataJson; }
                        }
                        return r.DataJson ?? "";
                    }).ToList().AsReadOnly();
                }
            }
            catch
            {
                // CBOR deserialization fallback
            }
        }

        return [];
    }

    public async Task Append(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        var version = await GetNextVersion(streamId, ct).ConfigureAwait(false);

        foreach (var evt in events)
        {
            version++;
            var dataJson = JsonSerializer.Serialize(evt, JsonOptions);
            var record = new EventRecord
            {
                StreamId = streamId,
                Version = version,
                EventType = evt.GetType().Name,
                DataJson = dataJson,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _logger.LogDebug("Appending event {EventType} to stream {StreamId} (version {Version})",
                evt.GetType().Name, streamId, version);

            // Use the SDK's typed Create method with CBOR serialization instead of raw SurrealQL.
            // The [Column] attributes on EventRecord ensure CBOR uses snake_case field names
            // matching the mt_events schema.
            await _session.Create("mt_events", record, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        await Append(streamId, events, ct).ConfigureAwait(false);
        return streamId;
    }

    public async Task<IReadOnlyList<(string StreamId, object Event, long Version)>> FetchAllAfterVersion(
        long version, CancellationToken ct = default)
    {
        var response = await _session.RawQuery(
            $"SELECT * FROM mt_events WHERE version > {version} ORDER BY version ASC;",
            null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            try
            {
                var records = response.GetValue<List<EventRecord>>(0);
                if (records is { Count: > 0 })
                {
                    var results = new List<(string, object, long)>();
                    foreach (var r in records)
                    {
                        object? evt = r.DataJson;
                        if (!string.IsNullOrEmpty(r.DataJson))
                        {
                            try { evt = JsonSerializer.Deserialize<object>(r.DataJson, JsonOptions) ?? r.DataJson; }
                            catch { evt = r.DataJson; }
                        }
                        results.Add((r.StreamId, evt ?? "", r.Version));
                    }
                    _logger.LogDebug("Fetched {Count} events after version {Version}", results.Count, version);
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
}

internal class EventRecord
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
    public DateTimeOffset CreatedAt { get; set; }
}
