using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
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
            query.TableName = MetadataDispatch.GetTableName(q.ElementType);
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

    // --- Public entry points (expression-only, for IQueryProvider backward compat) ---

    public async Task<List<T>> ToListAsync<T>(Expression expression, CancellationToken ct = default)
        => await ToListAsyncInternal<T>(expression, null, null, ct);

    public async Task<T?> FirstOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
        => await FirstOrDefaultAsyncInternal<T>(expression, null, null, ct);

    public async Task<T?> SingleOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
        => await SingleOrDefaultAsyncInternal<T>(expression, null, null, ct);

    // --- Internal entry points (accept fetch/include from SurrealDbQueryable) ---

    internal async Task<List<T>> ToListAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor> includeDescriptors,
        CancellationToken ct = default)
        => await ToListAsyncInternal<T>(expression, fetchFields, includeDescriptors, ct);

    internal async Task<T?> FirstOrDefaultAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor> includeDescriptors,
        CancellationToken ct = default)
        => await FirstOrDefaultAsyncInternal<T>(expression, fetchFields, includeDescriptors, ct);

    internal async Task<T?> SingleOrDefaultAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor> includeDescriptors,
        CancellationToken ct = default)
        => await SingleOrDefaultAsyncInternal<T>(expression, fetchFields, includeDescriptors, ct);

    // --- Core implementation (shared between public and internal entry points) ---

    private async Task<List<T>> ToListAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor>? includeDescriptors,
        CancellationToken ct)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
        }

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        var surql = query.ToSurrealQL();
        _logger.LogDebug("ToSurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null)
            {
                // Process Include descriptors (post-query client-side eager loading)
                if (includeDescriptors is { Count: > 0 } && raw.Count > 0)
                    await ProcessIncludesAsync(raw, includeDescriptors, ct).ConfigureAwait(false);
                return raw;
            }
        }

        return [];
    }

    private async Task<T?> FirstOrDefaultAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor>? includeDescriptors,
        CancellationToken ct)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
        }

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        query.Limit = 1;
        var surql = query.ToSurrealQL();
        _logger.LogDebug("FirstOrDefaultAsync SurrealQL: {Surql}", surql);
        var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null && raw.Count > 0)
            {
                // Process Include descriptors on the single result
                if (includeDescriptors is { Count: > 0 })
                    await ProcessIncludesAsync(raw, includeDescriptors, ct).ConfigureAwait(false);
                return raw[0];
            }
        }

        return default;
    }

    private async Task<T?> SingleOrDefaultAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SurrealDbQueryable<T>.IncludeDescriptor>? includeDescriptors,
        CancellationToken ct)
    {
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        ExtractTable(expression, query);

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

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

                if (raw.Count == 1)
                {
                    // Process Include descriptors on the single result
                    if (includeDescriptors is { Count: > 0 })
                        await ProcessIncludesAsync(raw, includeDescriptors, ct).ConfigureAwait(false);
                    return raw[0];
                }

                return default;
            }
        }

        return default;
    }

    /// <summary>
    /// Post-query phase: batch-loads documents referenced by Include descriptors
    /// and dispatches them via callbacks or dictionary population.
    /// </summary>
    private async Task ProcessIncludesAsync<T>(
        List<T> results,
        List<SurrealDbQueryable<T>.IncludeDescriptor> includes,
        CancellationToken ct)
    {
        foreach (var include in includes)
        {
            // 1. Extract distinct key values from results
            var prop = typeof(T).GetProperty(include.PropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is null) continue;

            var keys = new HashSet<object?>();
            foreach (var item in results)
            {
                var val = prop.GetValue(item);
                if (val is not null)
                    keys.Add(val);
            }

            if (keys.Count == 0) continue;

            // 2. Batch-load included documents using typed deserialization
            var targetTable = MetadataDispatch.GetTableName(include.IncludeType);
            var keyList = string.Join(", ", keys.Select(k =>
            {
                if (k is string s) return $"'{s.Replace("'", "\\'")}'";
                if (k is Guid g) return $"'{g}'";
                if (k is RecordId rid)
                {
                    return rid switch
                    {
                        RecordIdOf<string> sRid => $"'{sRid.Id.Replace("'", "\\'")}'",
                        RecordIdOf<long> lRid => $"{lRid.Id}",
                        RecordIdOf<int> iRid => $"{iRid.Id}",
                        _ => $"'{rid}'"
                    };
                }
                return $"{k}";
            }));
            // Use meta::id() to extract the string portion of RecordId for comparison.
            var surql = $"SELECT * FROM `{targetTable}` WHERE meta::id(id) IN [{keyList}];";
            _logger.LogDebug("Include SurrealQL: {Surql}", surql);

            var response = await _session.RawQuery(surql, null, ct).ConfigureAwait(false);

            // Check for query errors — the Include SQL might fail with the in-memory engine
            if (response.HasErrors)
            {
                _logger.LogWarning("Include query had errors. SurQL: {Surql}", surql);
                continue;
            }

            if (response.Count == 0) continue;

            // 3. Deserialize included documents using CBOR (avoids the broken
            //    ReadOnlyRecordIdJsonConverter.Read path entirely).
            //    We use GetValue<List<TInclude>>(0) via reflection for proper CBOR deserialization.
            var listType = typeof(List<>).MakeGenericType(include.IncludeType);
            var getValueMethod = typeof(SurrealDbResponse).GetMethods()
                .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(int));
            if (getValueMethod is null) continue;

            var typedGetValue = getValueMethod.MakeGenericMethod(listType);
            object? includedListObj;
            try
            {
                includedListObj = typedGetValue.Invoke(response, [0]);
            }
            catch (TargetInvocationException tie)
            {
                throw new InvalidOperationException(
                    $"CBOR deserialization of List<{include.IncludeType.Name}> failed: " +
                    $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}", tie);
            }

            if (includedListObj is not System.Collections.IEnumerable includedEnumerable)
                continue;

            var idProp = include.IncludeType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            var docById = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var typedDoc in includedEnumerable)
            {
                if (typedDoc is null) continue;
                var docId = idProp?.GetValue(typedDoc);
                var strKey = ExtractKeyString(docId);
                if (strKey is not null && !docById.ContainsKey(strKey))
                    docById[strKey] = typedDoc;
            }

            // 4. For each source result, extract foreign key string, lookup, and dispatch
            foreach (var item in results)
            {
                var rawKey = prop.GetValue(item);
                var strKey = ExtractKeyString(rawKey);
                if (strKey is null || !docById.TryGetValue(strKey, out var matchedDoc))
                    continue;

                if (include.Callback is not null)
                {
                    include.Callback.DynamicInvoke(matchedDoc);
                }
                else if (include.Dictionary is not null)
                {
                    // Pass the original (non-stringified) key to preserve the dictionary key type
                    var dictType = include.Dictionary.GetType();
                    var addMethod = dictType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
                    addMethod?.Invoke(include.Dictionary, [rawKey, matchedDoc]);
                }
            }
        }
    }

    /// <summary>
    /// Extracts a string key from a value for dictionary-based Include matching.
    /// Handles RecordId, string, Guid, and primitive types.
    /// </summary>
    private static string? ExtractKeyString(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is Guid g) return g.ToString();
        if (value is RecordIdOf<string> sRid) return sRid.Id;
        if (value is RecordIdOf<long> lRid) return lRid.Id.ToString();
        if (value is RecordIdOf<int> iRid) return iRid.Id.ToString();
        if (value is RecordId rid)
        {
            try { return rid.DeserializeId<string>(); } catch { }
            try { return rid.DeserializeId<long>().ToString(); } catch { }
            try { return rid.DeserializeId<int>().ToString(); } catch { }
            return rid.Table;
        }
        return value.ToString();
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
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
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
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
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
