using SurrealDb.Net;

namespace Dali;

internal class DaliDiagnostics : IDiagnostics
{
    private readonly ISurrealDbClient _client;

    public DaliDiagnostics(ISurrealDbClient client)
    {
        _client = client;
    }

    public Task<string> PreviewCommandAsync<T>(IQueryable<T> query, CancellationToken ct = default) where T : class
    {
        if (query.Expression is { } expr)
        {
            var visitor = new SurrealExpressionVisitor();
            var result = visitor.Translate(expr);
            return Task.FromResult(result.ToSurrealQL());
        }
        return Task.FromResult("null");
    }

    public async Task<string> ExplainPlanAsync<T>(IQueryable<T> query, CancellationToken ct = default) where T : class
    {
        string surql;
        if (query.Expression is { } expr)
        {
            var visitor = new SurrealExpressionVisitor();
            var result = visitor.Translate(expr);
            surql = result.ToSurrealQL();
        }
        else
        {
            return "Unable to generate EXPLAIN: query provider did not produce expression.";
        }

        if (string.IsNullOrWhiteSpace(surql))
            return "Unable to generate EXPLAIN: query provider produced empty SurrealQL.";

        var explain = $"EXPLAIN {surql}";
        var response = await _client.RawQuery(explain, null, ct).ConfigureAwait(false);
        if (response.HasErrors)
            return System.Text.Json.JsonSerializer.Serialize(new { errors = response.Errors });
        var values = response.GetValue<List<object>>(0);
        return System.Text.Json.JsonSerializer.Serialize(values);
    }
}
