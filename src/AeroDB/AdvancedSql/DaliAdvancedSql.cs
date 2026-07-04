using System.Runtime.CompilerServices;
using SurrealDb.Net.Models.Response;

namespace AeroDB;

/// <summary>
/// Advanced SQL query API for multi-document tuple queries and result streaming.
/// Access via <c>session.AdvancedSql</c>.
/// </summary>
public class DaliAdvancedSql
{
    private readonly InternalSessionBase _session;

    internal DaliAdvancedSql(InternalSessionBase session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Execute a raw SurrealQL query returning a 2-tuple of result sets.
    /// Uses multi-statement SurrealQL — each result set at a different response index.
    /// </summary>
    public async Task<(IReadOnlyList<T1>, IReadOnlyList<T2>)> QueryAsync<T1, T2>(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T1 : class where T2 : class
    {
        var response = await _session.Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors) throw CreateError(response.Errors);
        var r1 = DeserializeList<T1>(response, 0);
        var r2 = DeserializeList<T2>(response, 1);
        return (r1, r2);
    }

    /// <summary>3-tuple variant.</summary>
    public async Task<(IReadOnlyList<T1>, IReadOnlyList<T2>, IReadOnlyList<T3>)> QueryAsync<T1, T2, T3>(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class
    {
        var response = await _session.Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors) throw CreateError(response.Errors);
        var r1 = DeserializeList<T1>(response, 0);
        var r2 = DeserializeList<T2>(response, 1);
        var r3 = DeserializeList<T3>(response, 2);
        return (r1, r2, r3);
    }

    /// <summary>4-tuple variant.</summary>
    public async Task<(IReadOnlyList<T1>, IReadOnlyList<T2>, IReadOnlyList<T3>, IReadOnlyList<T4>)> QueryAsync<T1, T2, T3, T4>(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
        where T1 : class where T2 : class where T3 : class where T4 : class
    {
        var response = await _session.Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors) throw CreateError(response.Errors);
        var r1 = DeserializeList<T1>(response, 0);
        var r2 = DeserializeList<T2>(response, 1);
        var r3 = DeserializeList<T3>(response, 2);
        var r4 = DeserializeList<T4>(response, 3);
        return (r1, r2, r3, r4);
    }

    /// <summary>
    /// Execute a raw SurrealQL query and stream results as <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class
    {
        var response = await _session.Session.RawQuery(sql, parameters, ct).ConfigureAwait(false);
        if (response.HasErrors) yield break;
        var results = DeserializeList<T>(response, 0);
        foreach (var item in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static IReadOnlyList<T> DeserializeList<T>(SurrealDbResponse response, int index)
        where T : class
    {
        try
        {
            var list = response.GetValue<List<T>>(index);
            return list?.AsReadOnly() ?? (IReadOnlyList<T>)Array.Empty<T>();
        }
        catch
        {
            // CBOR deserialization fallback — try JSON round-trip
            try
            {
                var raw = response.GetValue<List<object>>(index);
                if (raw is null) return Array.Empty<T>();
                var json = System.Text.Json.JsonSerializer.Serialize(raw);
                var result = System.Text.Json.JsonSerializer.Deserialize<List<T>>(json);
                return result?.AsReadOnly() ?? (IReadOnlyList<T>)Array.Empty<T>();
            }
            catch
            {
                return Array.Empty<T>();
            }
        }
    }

    private static InvalidOperationException CreateError(IEnumerable<ISurrealDbErrorResult> errors)
    {
        var messages = string.Join("; ", errors.Select(e => e switch
        {
            SurrealDbErrorResult err => err.Details,
            SurrealDbProtocolErrorResult err => $"{err.Details} ({err.Description})",
            _ => $"Unknown error: {e.GetType().Name}"
        }));
        return new InvalidOperationException($"SurrealDB query error: {messages}");
    }
}
