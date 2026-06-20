// ============================================================
// SurrealDB EF Core LINQ Provider
// SurrealContext, HTTP Executor, Graph API
// ============================================================

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SurrealEFCore.Core;
using SurrealEFCore.Infrastructure;
using SurrealEFCore.Query;

namespace SurrealEFCore.Infrastructure;

// ── Connection Options ────────────────────────────────────────

public class SurrealDbOptions
{
    public string Url       { get; set; } = "http://localhost:8000";
    public string Namespace { get; set; } = "test";
    public string Database  { get; set; } = "test";
    public string Username  { get; set; } = "root";
    public string Password  { get; set; } = "root";
}

// ── HTTP Query Executor ───────────────────────────────────────

/// <summary>
/// Sends SurrealQL to the SurrealDB HTTP /sql endpoint
/// and deserialises results.
/// </summary>
public class SurrealHttpExecutor : ISurrealQueryExecutor, IDisposable
{
    private readonly HttpClient       _http;
    private readonly SurrealDbOptions _opts;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public SurrealHttpExecutor(SurrealDbOptions opts)
    {
        _opts = opts;
        _http = new HttpClient { BaseAddress = new Uri(opts.Url) };

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{opts.Username}:{opts.Password}"));

        _http.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
        _http.DefaultRequestHeaders.Add("NS", opts.Namespace);
        _http.DefaultRequestHeaders.Add("DB", opts.Database);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<IEnumerable<T>> ExecuteAsync<T>(
        TranslatedQuery query,
        CancellationToken ct = default)
    {
        var response = await PostSurrealQL(query.SurrealQL, ct);
        return ParseResult<T>(response);
    }

    public async Task<T?> ExecuteScalarAsync<T>(
        TranslatedQuery query,
        CancellationToken ct = default)
    {
        var results = await ExecuteAsync<T>(query, ct);
        return results.FirstOrDefault();
    }

    public async Task<string> ExecuteRawAsync(string surql, CancellationToken ct = default)
    {
        return await PostSurrealQL(surql, ct);
    }

    private async Task<string> PostSurrealQL(string surql, CancellationToken ct)
    {
        using var content  = new StringContent(surql, Encoding.UTF8, "application/text");
        using var response = await _http.PostAsync("/sql", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private IEnumerable<T> ParseResult<T>(string json)
    {
        // SurrealDB returns: [{ "status": "OK", "result": [...] }]
        using var doc   = JsonDocument.Parse(json);
        var       root  = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return Enumerable.Empty<T>();

        var first = root[0];
        if (!first.TryGetProperty("result", out var result))
            return Enumerable.Empty<T>();

        if (result.ValueKind == JsonValueKind.Array)
            return result.Deserialize<List<T>>(_json) ?? new();

        if (result.ValueKind != JsonValueKind.Null)
        {
            var single = result.Deserialize<T>(_json);
            return single is not null ? new[] { single } : Enumerable.Empty<T>();
        }

        return Enumerable.Empty<T>();
    }

    public void Dispose() => _http.Dispose();
}

// ── SurrealContext (DbContext equivalent) ─────────────────────

/// <summary>
/// The central unit-of-work: owns DbSets, the graph API,
/// and raw SurrealQL execution.
/// </summary>
public class SurrealContext : IDisposable
{
    private readonly SurrealHttpExecutor _executor;
    private readonly SurrealQueryProvider _provider;

    public SurrealContext(SurrealDbOptions options)
    {
        _executor = new SurrealHttpExecutor(options);
        _provider = new SurrealQueryProvider(_executor);

        // ── Document sets ──────────────────────────────────────
        People   = new SurrealDbSet<Person>  (this, _provider);
        Products = new SurrealDbSet<Product> (this, _provider);

        // ── Graph API ──────────────────────────────────────────
        Graph = new SurrealGraphApi(this);
    }

    // ── DbSets ────────────────────────────────────────────────
    public SurrealDbSet<Person>  People   { get; }
    public SurrealDbSet<Product> Products { get; }

    // ── Graph API ─────────────────────────────────────────────
    public SurrealGraphApi Graph { get; }

    // ── Raw execution ─────────────────────────────────────────

    public Task ExecuteRawAsync(string surql, CancellationToken ct = default)
        => _executor.ExecuteRawAsync(surql, ct);

    public async Task<T?> QuerySingleAsync<T>(string surql, CancellationToken ct = default)
    {
        var query = new TranslatedQuery(surql, QueryMode.SQL, ElementType: typeof(T));
        return await _executor.ExecuteScalarAsync<T>(query, ct);
    }

    public async Task<List<T>> QueryAsync<T>(string surql, CancellationToken ct = default)
    {
        var query = new TranslatedQuery(surql, QueryMode.SQL, ElementType: typeof(T));
        var res   = await _executor.ExecuteAsync<T>(query, ct);
        return res.ToList();
    }

    // ── Transactions ──────────────────────────────────────────

    public async Task<T> TransactAsync<T>(
        Func<SurrealContext, Task<T>> operation,
        CancellationToken ct = default)
    {
        await ExecuteRawAsync("BEGIN TRANSACTION;", ct);
        try
        {
            var result = await operation(this);
            await ExecuteRawAsync("COMMIT TRANSACTION;", ct);
            return result;
        }
        catch
        {
            await ExecuteRawAsync("CANCEL TRANSACTION;", ct);
            throw;
        }
    }

    public void Dispose() => _executor.Dispose();
}

// ── Graph API ─────────────────────────────────────────────────

/// <summary>
/// Fluent graph traversal and RELATE/edge management API.
/// </summary>
public class SurrealGraphApi
{
    private readonly SurrealContext _ctx;

    public SurrealGraphApi(SurrealContext ctx) => _ctx = ctx;

    // ── Create edges ──────────────────────────────────────────

    /// <summary>RELATE in:id -> edge -> out:id CONTENT {...}</summary>
    public async Task<TEdge> RelateAsync<TEdge>(
        RecordId  from,
        RecordId  to,
        TEdge     edge,
        CancellationToken ct = default)
        where TEdge : SurrealEntity
    {
        var edgeName = typeof(TEdge).Name.ToLower();
        var edgeId   = string.IsNullOrEmpty(edge.Id.Id)
                         ? $"{edgeName}:{Guid.NewGuid():N}"
                         : edge.Id.ToString();

        var content = JsonSerializer.Serialize(edge, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        var surql = $"RELATE {from}->{edgeName}->{to} CONTENT {content};";
        await _ctx.ExecuteRawAsync(surql, ct);
        return edge;
    }

    // ── Traverse ──────────────────────────────────────────────

    /// <summary>
    /// Traverse outbound: SELECT ->edge->target.* FROM source
    /// </summary>
    public async Task<List<TTarget>> TraverseOutAsync<TEdge, TTarget>(
        RecordId   from,
        string?    whereClause  = null,
        int?       depth        = null,
        CancellationToken ct    = default)
        where TEdge  : SurrealEntity
        where TTarget: SurrealEntity
    {
        var edge   = typeof(TEdge).Name.ToLower();
        var target = typeof(TTarget).Name.ToLower();
        var path   = depth is > 1
            ? string.Concat(Enumerable.Repeat($"->{edge}->{target}", depth.Value))
            : $"->{edge}->{target}";

        var surql = $"SELECT {path}.* FROM {from}";
        if (whereClause is not null)
            surql += $" WHERE {whereClause}";
        surql += ";";

        return await _ctx.QueryAsync<TTarget>(surql, ct);
    }

    /// <summary>
    /// Traverse inbound: SELECT <-edge<-source.* FROM target
    /// </summary>
    public async Task<List<TSource>> TraverseInAsync<TEdge, TSource>(
        RecordId   to,
        string?    whereClause = null,
        CancellationToken ct   = default)
        where TEdge  : SurrealEntity
        where TSource: SurrealEntity
    {
        var edge   = typeof(TEdge).Name.ToLower();
        var source = typeof(TSource).Name.ToLower();
        var surql  = $"SELECT <-{edge}<-{source}.* FROM {to}";
        if (whereClause is not null)
            surql += $" WHERE {whereClause}";
        surql += ";";

        return await _ctx.QueryAsync<TSource>(surql, ct);
    }

    // ── Shortest path ─────────────────────────────────────────

    public async Task<List<string>> ShortestPathAsync(
        RecordId from,
        RecordId to,
        string   edgeName,
        CancellationToken ct = default)
    {
        // Uses SurrealDB's built-in graph path functions
        var surql = $"""
            SELECT array::join(
                SELECT VALUE id FROM (
                    SELECT * FROM path WHERE
                        from = {from} AND to = {to}
                        AND edge = {edgeName}
                )
            ) AS path FROM ONLY 1;
        """;
        var result = await _ctx.QueryAsync<Dictionary<string, string>>(surql, ct);
        return result.Select(r => r.GetValueOrDefault("path", "")).ToList();
    }

    // ── Delete edge ───────────────────────────────────────────

    public Task DeleteEdgeAsync(RecordId edgeId, CancellationToken ct = default)
        => _ctx.ExecuteRawAsync($"DELETE {edgeId};", ct);
}
