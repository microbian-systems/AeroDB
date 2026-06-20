using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using Dahomey.Cbor.Attributes;
using Dahomey.Cbor.ObjectModel;
using Dali.Metadata;
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
    /// Checks whether a type has a TenantId string property.
    /// Uses generated metadata when available, falls back to reflection.
    /// </summary>
    internal static bool HasTenantProperty(Type type)
        => MetadataDispatch.HasTenantId(type);

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

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        query.OrderBy.Clear();
        query.Limit = null;
        query.Skip = null;
        query.GroupAll = true;

        // The visitor already generated the correct server-side projection
        // (e.g., math::sum(Price)). Let it flow through to SurrealQL.
        var surql = query.ToSurrealQL();
        _logger.LogDebug("AggregateAsync ({Function}) SurrealQL: {Surql}", function, surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var result = TryExtractDecimal(response, function);
            if (result.HasValue)
                return result.Value;
        }

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
        query.Projection = "count()";
        query.GroupAll = true;

        var surql = query.ToSurrealQL();
        _logger.LogDebug("CountAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var result = TryExtractCount(response);
            if (result.HasValue)
                return result.Value;
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

    /// <summary>
    /// Extracts a decimal value from a SurrealDB aggregate response.
    /// Tries multiple deserialization strategies to handle different CBOR type mappings.
    /// </summary>
    private static decimal? TryExtractDecimal(SurrealDbResponse response, string function)
    {
        // Try DTO approach first — this is the most reliable path
        var dtoResult = TryExtractViaDto(response, function);
        if (dtoResult.HasValue)
            return dtoResult.Value;

        // Fallback: generic JSON extraction
        var raw = response.GetValue<List<object>>(0);
        if (raw is null || raw.Count == 0)
            return null;

        foreach (var item in raw)
        {
            if (item is CborValue cv)
            {
                var r = ExtractNumericFromCborValue(cv);
                if (r.HasValue) return r.Value;
            }

            if (item is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    var val = dict[key];
                    if (val is not null)
                        return Convert.ToDecimal(val);
                }
            }

            // Last resort: System.Text.Json serialization
            var json = JsonSerializer.Serialize(item, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        return prop.Value.GetDecimal();
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Number)
            {
                return doc.RootElement.GetDecimal();
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a count value from a SurrealDB count() response.
    /// Deserializes via CountResultDto (mapped to lowercase "count" key via CborProperty).
    /// </summary>
    private static int? TryExtractCount(SurrealDbResponse response)
    {
        try
        {
            var raw = response.GetValue<List<CountResultDto>>(0);
            if (raw is not null && raw.Count > 0)
                return (int)raw[0].Count;
        }
        catch
        {
            // Fall through
        }
        return null;
    }

    /// <summary>
    /// DTO for count() aggregate results: [{ "count": 3 }]
    /// The CBOR key is lowercase "count" — not PascalCase — so we
    /// need an explicit CborProperty override.
    /// </summary>
    private sealed class CountResultDto
    {
        [CborProperty("count")]
        public long Count { get; set; }
    }

    /// <summary>
    /// Tries to extract decimal via DTO specific to the aggregate function.
    /// </summary>
    private static decimal? TryExtractViaDto(SurrealDbResponse response, string function)
    {
        try
        {
            return function switch
            {
                "math::sum" => response.GetValue<List<SumResultDto>>(0)?.FirstOrDefault()?.Sum,
                "math::min" => response.GetValue<List<MinResultDto>>(0)?.FirstOrDefault()?.Min,
                "math::max" => response.GetValue<List<MaxResultDto>>(0)?.FirstOrDefault()?.Max,
                "math::mean" => response.GetValue<List<MeanResultDto>>(0)?.FirstOrDefault()?.Mean,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class SumResultDto
    {
        [CborProperty("math::sum")]
        public decimal Sum { get; set; }
    }

    private sealed class MinResultDto
    {
        [CborProperty("math::min")]
        public decimal Min { get; set; }
    }

    private sealed class MaxResultDto
    {
        [CborProperty("math::max")]
        public decimal Max { get; set; }
    }

    private sealed class MeanResultDto
    {
        [CborProperty("math::mean")]
        public decimal Mean { get; set; }
    }

    /// <summary>
    /// Extracts a decimal from a CborValue, handling all numeric subtypes.
    /// </summary>
    private static decimal? ExtractNumericFromCborValue(CborValue cv)
    {
        try
        {
            var json = cv.ToString();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        return prop.Value.GetDecimal();
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Number)
            {
                return doc.RootElement.GetDecimal();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts a long from a CborValue.
    /// </summary>
    private static long? ExtractLongFromCborValue(CborValue cv)
    {
        try
        {
            var json = cv.ToString();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        return (int)prop.Value.GetInt64();
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Number)
            {
                return (int)doc.RootElement.GetInt64();
            }
        }
        catch { }
        return null;
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
