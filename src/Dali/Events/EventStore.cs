using System.Text.Json;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class EventStore : IEvents
{
    private readonly ISurrealDbSession _session;

    public EventStore(ISurrealDbSession session)
        => _session = session;

    public async Task<IReadOnlyList<object>> FetchStream(string streamId, CancellationToken ct = default)
    {
        var response = await _session.RawQuery(
            $"SELECT * FROM mt_events WHERE stream_id = '{streamId}' ORDER BY version ASC;",
            null, ct);

        if (!response.HasErrors && response.Count > 0)
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

        return [];
    }

    public async Task Append(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        var version = await GetNextVersion(streamId, ct);

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
            await _session.RawQuery($"CREATE mt_events CONTENT {recordJson};", null, ct);
        }
    }

    public async Task<string> StartStream(string streamId, IEnumerable<object> events, CancellationToken ct = default)
    {
        await Append(streamId, events, ct);
        return streamId;
    }

    private async Task<long> GetNextVersion(string streamId, CancellationToken ct)
    {
        var response = await _session.RawQuery(
            $"SELECT math::max(version) AS max_ver FROM mt_events WHERE stream_id = '{streamId}';",
            null, ct);

        if (!response.HasErrors && response.Count > 0)
        {
            var firstResult = response.GetValue<object>(0);
            if (firstResult is not null)
            {
                var json = JsonSerializer.Serialize(firstResult, JsonOptions);
                var dicts = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, JsonOptions);
                if (dicts is not null && dicts.Count > 0 && dicts[0].TryGetValue("max_ver", out var maxVer)
                    && maxVer.ValueKind != JsonValueKind.Null)
                {
                    return maxVer.GetInt64();
                }
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
