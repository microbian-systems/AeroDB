using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
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
    private readonly InternalSessionBase? _sessionBase;
    private readonly StoreOptions _options;
    private readonly string? _tenantId;
    private readonly ILogger<SurrealQueryProvider> _logger;

    /// <summary>
    /// Creates a query provider bound to the given session and store options.
    /// </summary>
    public SurrealQueryProvider(ISurrealDbSession session, StoreOptions options, string? tenantId = null)
        : this(session, sessionBase: null, options, tenantId)
    {
    }

    /// <summary>
    /// Creates a query provider with optional <see cref="InternalSessionBase"/> for schema-based
    /// session routing. When <paramref name="sessionBase"/> is provided, queries are routed
    /// through the correct forked session based on the element type's schema mapping.
    /// </summary>
    public SurrealQueryProvider(ISurrealDbSession session, InternalSessionBase? sessionBase, StoreOptions options, string? tenantId = null)
    {
        _session = session;
        _sessionBase = sessionBase;
        _options = options;
        _tenantId = tenantId;
        _logger = options.LoggerFactory?.CreateLogger<SurrealQueryProvider>()
            ?? NullLogger<SurrealQueryProvider>.Instance;
    }

    /// <summary>
    /// The underlying SurrealDB session for raw query access.
    /// Internal to allow SearchExtensions and GraphQueryProvider to bypass the LINQ provider.
    /// </summary>
    internal ISurrealDbSession Session => _session;

    /// <summary>
    /// Resolves the correct <see cref="ISurrealDbSession"/> for the given element type
    /// based on its schema mapping (database). When no schema is configured or when
    /// no <see cref="InternalSessionBase"/> is available, falls back to <see cref="_session"/>.
    /// </summary>
    private async Task<ISurrealDbSession> GetSessionForElementType(Type elementType, CancellationToken ct)
    {
        if (_sessionBase is null)
            return _session;

        var (schemaName, _) = MetadataDispatch.GetSchemaTarget(elementType, _options.Schema);
        return await _sessionBase.GetSessionForSchemaAsync(schemaName, ct).ConfigureAwait(false);
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
    /// Extracts the <see cref="SurrealDbQueryable{T}.ViewName"/> from the source
    /// queryable in the expression tree, if set.
    /// </summary>
    internal static string? ExtractViewName(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SurrealDbQueryable<>))
            {
                var viewNameProp = qType.GetProperty("ViewName",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return viewNameProp?.GetValue(q) as string;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractViewName(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractViewName(u.Operand);
        return null;
    }

    /// <summary>
    /// Applies a tenant filter to the query if tenancy is active and the target type supports it.
    /// DatabasePerTenant isolates at the database level — no WHERE filter needed.
    /// </summary>
    internal void ApplyTenantFilter(SurrealQueryResult query, Type? elementType)
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
    internal void ApplySoftDeleteFilter(SurrealQueryResult query, Type? elementType)
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

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        var hasIncludes = includeDescriptors is { Count: > 0 };

        string surql;
        if (hasIncludes)
        {
            // Build multi-statement SurrealQL using LET variable for server-side
            // single-round-trip eager loading (analogous to Marten's temp tables).
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            foreach (var include in includeDescriptors!)
            {
                var targetTable = MetadataDispatch.GetTableName(include.IncludeType);
                sb.Append("SELECT * FROM `")
                  .Append(targetTable)
                  .Append("` WHERE id IN (SELECT VALUE `")
                  .Append(include.PropertyName)
                  .Append("` FROM $main);");
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        _logger.LogDebug("ToSurrealQL: {Surql}", surql);
        var querySession = await GetSessionForElementType(typeof(T), ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes)
        {
            // Multi-statement: index 0 = LET result (if any), index 1 = main results, 2+ = includes
            // Note: Don't check HasErrors here - individual include sub-statements may have
            // their own status records that don't indicate actual query failure.
            return DeserializeMainAndIncludes(response, includeDescriptors!, ct);
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null)
                return raw;
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

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        query.Limit = 1;

        var hasIncludes = includeDescriptors is { Count: > 0 };

        string surql;
        if (hasIncludes)
        {
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            foreach (var include in includeDescriptors!)
            {
                var targetTable = MetadataDispatch.GetTableName(include.IncludeType);
                sb.Append("SELECT * FROM `")
                  .Append(targetTable)
                  .Append("` WHERE id IN (SELECT VALUE `")
                  .Append(include.PropertyName)
                  .Append("` FROM $main);");
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        _logger.LogDebug("FirstOrDefaultAsync SurrealQL: {Surql}", surql);
        var querySession = await GetSessionForElementType(typeof(T), ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes)
        {
            var results = DeserializeMainAndIncludes(response, includeDescriptors!, ct);
            if (results.Count > 0)
                return results[0];
            return default;
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null && raw.Count > 0)
                return raw[0];
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

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        ApplyTenantFilter(query, typeof(T));
        ApplySoftDeleteFilter(query, typeof(T));

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        query.Limit = 2; // fetch 2 to detect > 1 result

        var hasIncludes = includeDescriptors is { Count: > 0 };

        string surql;
        if (hasIncludes)
        {
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            foreach (var include in includeDescriptors!)
            {
                var targetTable = MetadataDispatch.GetTableName(include.IncludeType);
                sb.Append("SELECT * FROM `")
                  .Append(targetTable)
                  .Append("` WHERE id IN (SELECT VALUE `")
                  .Append(include.PropertyName)
                  .Append("` FROM $main);");
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        _logger.LogDebug("SingleOrDefaultAsync SurrealQL: {Surql}", surql);
        var querySession = await GetSessionForElementType(typeof(T), ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes)
        {
            var results = DeserializeMainAndIncludes(response, includeDescriptors!, ct);
            if (results.Count > 1)
                throw new InvalidOperationException("Sequence contains more than one element.");
            if (results.Count == 1)
                return results[0];
            return default;
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null)
            {
                if (raw.Count > 1)
                    throw new InvalidOperationException("Sequence contains more than one element.");
                if (raw.Count == 1)
                    return raw[0];
                return default;
            }
        }

        return default;
    }

    /// <summary>
    /// Deserializes main results and included document sets from a multi-statement
    /// SurrealDbResponse produced by the LET-based approach.
    /// Dispatches included documents to callbacks or dictionaries on each main result.
    ///
    /// <para>Handles both engine behaviors: the embedded in-memory engine does not
    /// produce a separate result set for the LET statement (main at index 0),
    /// while the remote HTTP/WS engine does (main at index 1).</para>
    /// </summary>
    private List<T> DeserializeMainAndIncludes<T>(
        SurrealDbResponse response,
        List<SurrealDbQueryable<T>.IncludeDescriptor> includes,
        CancellationToken ct)
    {
        var includeCount = includes.Count;

        // Detect engine behavior: some engines (embedded) don't produce a result
        // for the LET statement, so response.Count = 1 + includeCount.
        // Remote engines (HTTP/WS) produce a LET result, so response.Count = 2 + includeCount.
        //   LET absent: [main, include1, include2, ...]
        //   LET present: [let_result, main, include1, include2, ...]
        int mainIndex;
        // Pragmatic heuristic: try index 0 first; if it yields results, use it.
        // If index 0 yields nothing and there are more result sets, try index 1.
        var testMain = response.GetValue<List<T>>(0);
        if (testMain is { Count: > 0 })
            mainIndex = 0;
        else if (response.Count > 1)
            mainIndex = 1;
        else
            return [];

        var results = mainIndex == 0 ? testMain : response.GetValue<List<T>>(mainIndex);
        if (results is null || results.Count == 0)
            return results ?? [];

        // Deserialize included docs starting after main results
        int resultIndex = mainIndex + 1;
        foreach (var include in includes)
        {
            if (resultIndex >= response.Count) break;

            var listType = typeof(List<>).MakeGenericType(include.IncludeType);
            var getValueMethod = typeof(SurrealDbResponse).GetMethods()
                .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(int));

            if (getValueMethod is null) { resultIndex++; continue; }

            var typedGetValue = getValueMethod.MakeGenericMethod(listType);
            object? includedListObj;
            try
            {
                // Check if this result is an OK result (not an error, e.g., non-existent table)
                if (response[resultIndex] is not SurrealDbOkResult)
                {
                    resultIndex++;
                    continue;
                }

                includedListObj = typedGetValue.Invoke(response, [resultIndex]);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is NotSupportedException)
            {
                resultIndex++;
                continue;
            }
            catch (TargetInvocationException tie)
            {
                throw new InvalidOperationException(
                    $"CBOR deserialization of List<{include.IncludeType.Name}> failed: " +
                    $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}", tie);
            }
            catch
            {
                resultIndex++;
                continue;
            }

            if (includedListObj is not IEnumerable includedEnumerable)
            {
                resultIndex++;
                continue;
            }

            // Build lookup: id → includedDoc
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

            // For each source result, extract FK, lookup, and dispatch
            var prop = typeof(T).GetProperty(include.PropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is null) { resultIndex++; continue; }

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

            resultIndex++;
        }

        return results;
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
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
            ExtractTable(expression, query);
        if (viewName is not null)
            query.TableName = viewName;

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
        var aggSession = await GetSessionForElementType(typeof(T), ct).ConfigureAwait(false);
        var response = await aggSession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

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
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
        }
        if (viewName is not null)
            query.TableName = viewName;

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
        var countSession = await GetSessionForElementType(elementType ?? typeof(object), ct).ConfigureAwait(false);
        var response = await countSession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var result = TryExtractCount(response);
            if (result.HasValue)
                return result.Value;
        }

        return 0;
    }

    /// <summary>
    /// Executes an ad-hoc aggregate query, applying tenant filters, soft-delete filters,
    /// schema session routing, and ViewName overrides.
    /// </summary>
    internal async Task<List<TResult>> ExecuteAggregateAsync<T, TResult>(
        AggregateQueryBuilder<T> builder,
        Expression sourceExpression,
        CancellationToken ct) where T : class
    {
        var visitor = new SurrealExpressionVisitor();
        var viewName = ExtractViewName(sourceExpression);
        visitor.ViewName = viewName;
        var result = visitor.Translate(sourceExpression);
        if (string.IsNullOrEmpty(result.TableName))
        {
            if (sourceExpression is ConstantExpression c && c.Value is IQueryable q)
                result.TableName = MetadataDispatch.GetTableName(q.ElementType);
        }
        if (viewName is not null)
            result.TableName = viewName;

        ApplyTenantFilter(result, typeof(T));
        ApplySoftDeleteFilter(result, typeof(T));

        // Override SELECT + GROUP BY with aggregate builder's values
        result.Projection = builder.BuildSelect();
        if (builder.GroupByClause is not null)
            result.GroupBy = builder.GroupByClause.Split(", ").ToList();

        var surql = result.ToSurrealQL();
        var querySession = await GetSessionForElementType(typeof(T), ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, result.Parameters, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<TResult>>(0);
            if (raw is not null) return raw;
        }
        return [];
    }

    public async Task<bool> AnyAsync(Expression expression, CancellationToken ct = default)
    {
        var visitor = CreateVisitor();
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType);
        }
        if (viewName is not null)
            query.TableName = viewName;

        var elementType = ExtractElementType(expression);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);
        query.Limit = 1;
        var surql = query.ToSurrealQL();
        _logger.LogDebug("AnyAsync SurrealQL: {Surql}", surql);
        var anySession = await GetSessionForElementType(elementType ?? typeof(object), ct).ConfigureAwait(false);
        var response = await anySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

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
