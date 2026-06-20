using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Dali;

public class SurrealQueryProvider : IQueryProvider
{
    private readonly ISurrealDbSession _session;
    private readonly StoreOptions _options;
    private readonly string? _tenantId;
    private readonly ILogger<SurrealQueryProvider> _logger;

    public SurrealQueryProvider(ISurrealDbSession session, StoreOptions options, string? tenantId = null)
    {
        _session = session;
        _options = options;
        _tenantId = tenantId;
        _logger = options.LoggerFactory?.CreateLogger<SurrealQueryProvider>()
            ?? NullLogger<SurrealQueryProvider>.Instance;
    }

    private SurrealExpressionVisitor CreateVisitor() => new();

    /// <summary>
    /// Cached check for whether a type implements <see cref="ISoftDeleted"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, bool> IsSoftDeletedCache = new();

    private static bool IsSoftDeletedType(Type type)
        => IsSoftDeletedCache.GetOrAdd(type, t => typeof(ISoftDeleted).IsAssignableFrom(t));

    /// <summary>
    /// Cached check for whether a type has a TenantId string property.
    /// </summary>
    internal static readonly ConcurrentDictionary<Type, bool> HasTenantCache = new();

    internal static bool HasTenantProperty(Type type)
        => HasTenantCache.GetOrAdd(type, static t =>
        {
            var prop = t.GetProperty("TenantId", typeof(string));
            return prop is not null && prop.CanRead && prop.CanWrite;
        });

    /// <summary>
    /// Extracts the table name from the expression and applies it to the query.
    /// </summary>
    private static void ExtractTable(Expression expression, SurrealQueryResult query)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
            query.TableName = ToSnakeCase(q.ElementType.Name);
    }

    /// <summary>
    /// Returns the element type from the innermost IQueryable in the expression tree.
    /// </summary>
    private static Type? ExtractElementType(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
            return q.ElementType;
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractElementType(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractElementType(u.Operand);
        return null;
    }

    /// <summary>
    /// Applies a tenant filter to the query if tenancy is active and the target type supports it.
    /// DatabasePerTenant isolates at the database level — no WHERE filter needed.
    /// </summary>
    private void ApplyTenantFilter(SurrealQueryResult query, Type? elementType)
    {
        if (string.IsNullOrEmpty(_tenantId) || elementType is null)
            return;

        if (_options.TenancyStyle == TenancyStyle.DatabasePerTenant)
            return;

        if (HasTenantProperty(elementType))
        {
            query.Where.Add($"TenantId = '{_tenantId?.Replace("'", "\\'")}'");
            _logger.LogDebug("Tenant filter applied: {TenantId}", _tenantId);
        }
    }

    /// <summary>
    /// Applies a soft-delete filter to the query, excluding documents where <c>Deleted = true</c>
    /// if the element type implements <see cref="ISoftDeleted"/> and soft-delete filtering is enabled.
    /// </summary>
    private void ApplySoftDeleteFilter(SurrealQueryResult query, Type? elementType)
    {
        if (elementType is null)
            return;

        if (!_options.SoftDeleteEnabled)
            return;

        if (IsSoftDeletedType(elementType))
        {
            query.Where.Add("Deleted = false");
            _logger.LogDebug("Soft-delete filter applied to {Type}", elementType.Name);
        }
    }

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
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        var surql = query.ToSurrealQL();
        _logger.LogDebug("ToSurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

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
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));
        query.Limit = 1;
        var surql = query.ToSurrealQL();
        _logger.LogDebug("FirstOrDefaultAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null && raw.Count > 0)
                return raw[0];
        }

        return default;
    }

    public async Task<T?> SingleOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        ExtractTable(expression, query);

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        query.Limit = 2; // fetch 2 to detect > 1 result
        var surql = query.ToSurrealQL();
        _logger.LogDebug("SingleOrDefaultAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null)
            {
                if (raw.Count > 1)
                    throw new InvalidOperationException("Sequence contains more than one element.");
                return raw.Count == 1 ? raw[0] : default;
            }
        }

        return default;
    }

    public async Task<decimal> AggregateAsync<T>(Expression expression, string fieldName, string function, CancellationToken ct = default)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
            ExtractTable(expression, query);

        var elementType = ExtractElementType(expression);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);

        query.OrderBy.Clear();
        query.Limit = null;
        query.Skip = null;

        // Fetch full records (select *) — CBOR deserializes known types reliably.
        // Then extract field values via reflection and aggregate client-side.
        query.Projection = "*";

        var surql = query.ToSurrealQL();
        _logger.LogDebug("AggregateAsync ({Function}) SurrealQL: {Surql}", function, surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count <= 0)
            return 0m;

        try
        {
            // CBOR properly deserializes to known Record-derived types (like T)
            var items = response.GetValue<List<T>>(0);
            if (items is not null && items.Count > 0)
            {
                var prop = typeof(T).GetProperty(fieldName);
                if (prop is not null)
                {
                    var values = items
                        .Select(item => prop.GetValue(item))
                        .Where(v => v is not null && v is IConvertible)
                        .Select(v => Convert.ToDecimal(v))
                        .ToList();

                    if (values.Count > 0)
                    {
                        return function switch
                        {
                            "math::sum" => values.Sum(),
                            "math::min" => values.Min(),
                            "math::max" => values.Max(),
                            "math::mean" => values.Average(),
                            _ => values.First()
                        };
                    }
                }
            }
        }
        catch { }

        return 0m;
    }

    public async Task<int> CountAsync(Expression expression, CancellationToken ct = default)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        var elementType = ExtractElementType(expression);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);

        // Strip ordering and limit — they don't affect count
        query.OrderBy.Clear();
        query.Limit = null;
        query.Skip = null;
        query.Projection = "*";

        var surql = query.ToSurrealQL();
        _logger.LogDebug("CountAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            // GetValue<List<object>> works for Record-derived types but not for
            // SELECT count() aggregates. Query all rows and count client-side.
            var raw = response.GetValue<List<object>>(0);
            return raw?.Count ?? 0;
        }

        return 0;
    }

    public async Task<bool> AnyAsync(Expression expression, CancellationToken ct = default)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = ToSnakeCase(q.ElementType.Name);
        }

        var elementType = ExtractElementType(expression);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);
        query.Limit = 1;
        var surql = query.ToSurrealQL();
        _logger.LogDebug("AnyAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<object>>(0);
            return raw is { Count: > 0 };
        }

        return false;
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
