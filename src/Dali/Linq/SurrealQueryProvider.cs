using System.Linq.Expressions;
using System.Text.Json;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class SurrealQueryProvider : IQueryProvider
{
    private readonly ISurrealDbSession _session;
    private readonly SurrealExpressionVisitor _visitor = new();

    public SurrealQueryProvider(ISurrealDbSession session)
        => _session = session;

    public IQueryable CreateQuery(Expression expression)
    {
        var elemType = expression.Type.GetGenericArguments().FirstOrDefault()
            ?? typeof(object);
        return (IQueryable)Activator.CreateInstance(
            typeof(SurrealDbQueryable<>).MakeGenericType(elemType),
            this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new SurrealDbQueryable<TElement>(this, expression);

    public object? Execute(Expression expression)
        => ExecuteSync<object>(expression);

    public TResult Execute<TResult>(Expression expression)
        => ExecuteSync<TResult>(expression);

    private TResult ExecuteSync<TResult>(Expression expression)
    {
        var task = ToListAsync<TResult>(expression);
        return task.GetAwaiter().GetResult().FirstOrDefault()!;
    }

    public async Task<List<T>> ToListAsync<T>(Expression expression, CancellationToken ct = default)
    {
        var query = _visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        var surql = query.ToSurrealQL();
        var response = await _session.RawQuery(surql, null, ct);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null)
                return raw;
        }

        return [];
    }

    public async Task<T?> FirstOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
    {
        var query = _visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        query.Limit = 1;
        var surql = query.ToSurrealQL();
        var response = await _session.RawQuery(surql, null, ct);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValues<T>(0);
            return raw.FirstOrDefault();
        }

        return default;
    }

    public async Task<int> CountAsync(Expression expression, CancellationToken ct = default)
    {
        var query = _visitor.Translate(expression);
        var table = query.TableName ?? "unknown";
        var surql = $"SELECT count() FROM {table} GROUP BY ALL;";
        var response = await _session.RawQuery(surql, null, ct);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<CountResult>>(0);
            if (raw is not null && raw.Count > 0)
                return raw[0].Count;
        }

        return 0;
    }

    public async Task<bool> AnyAsync(Expression expression, CancellationToken ct = default)
    {
        var result = await FirstOrDefaultAsync<object>(expression, ct);
        return result is not null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}

internal class CountResult
{
    public int Count { get; set; }
}
