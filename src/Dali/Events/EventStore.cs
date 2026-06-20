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
                var firstResult = response.GetValue<object>(0);
                if (firstResult is not null)
                {
                    var json = JsonSerializer.Serialize(firstResult, JsonOptions);
                    var records = JsonSerializer.Deserialize<List<EventRecord>>(json, JsonOptions);
                    return records?.Select(r =>
                    {
                        if (!string.IsNullOrEmpty(r.DataJson))
                        {
                            try { return JsonSerializer.Deserialize<object>(r.DataJson, JsonOptions) ?? r.DataJson; }
                            catch { return r.DataJson; }
                        }
                        return r.DataJson ?? "";
                    }).ToList().AsReadOnly() ?? [];
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

            var recordJson = JsonSerializer.Serialize(record, JsonOptions);
            _logger.LogDebug("Appending event {EventType} to stream {StreamId} (version {Version})",
                evt.GetType().Name, streamId, version);
            await _session.RawQuery($"CREATE mt_events CONTENT {recordJson};", null, ct).ConfigureAwait(false);
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
                var firstResult = response.GetValue<object>(0);
                if (firstResult is not null)
                {
                    var json = JsonSerializer.Serialize(firstResult, JsonOptions);
                    var records = JsonSerializer.Deserialize<List<EventRecord>>(json, JsonOptions);
                    if (records is not null)
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
                var firstResult = response.GetValue<object>(0);
                if (firstResult is not null)
                {
                    var json = JsonSerializer.Serialize(firstResult, JsonOptions);
                    var dicts = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, JsonOptions);
                    if (dicts is not null && dicts.Count > 0 && dicts[0].TryGetValue("version", out var ver)
                        && ver.ValueKind != JsonValueKind.Null)
                    {
                        return ver.GetInt64();
                    }
                }
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
    public string StreamId { get; set; } = "";
    public long Version { get; set; }
    public string EventType { get; set; } = "";
    public string? DataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
