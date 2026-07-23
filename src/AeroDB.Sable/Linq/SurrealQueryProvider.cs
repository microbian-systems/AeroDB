using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AeroDB.Sable.Internals.Cbor;
using AeroDB.Sable.Metadata;
using Dahomey.Cbor.Attributes;
using Dahomey.Cbor.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

internal sealed class SurrealQueryProvider : IQueryProvider
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

    internal InternalSessionBase? SessionBase => _sessionBase;

    internal StoreOptions StoreOptions => _options;

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

    private void LogSurrealQuery(string operation, string surql, SurrealQueryResult query, Type? elementType)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        if (query.Parameters is { Count: > 0 })
        {
            _logger.LogDebug(
                "{Operation} SurrealQL: {SurrealQL} Parameters: {@Parameters}",
                operation,
                surql,
                RedactParameters(query, elementType));
        }
        else
        {
            _logger.LogDebug("{Operation} SurrealQL: {SurrealQL}", operation, surql);
        }
    }

    private static IReadOnlyDictionary<string, object?> RedactParameters(SurrealQueryResult query, Type? elementType)
    {
        if (elementType is null)
            return query.Parameters;

        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in query.Parameters)
        {
            redacted[parameter.Key] = IsPersonalDataParameter(query, elementType, parameter.Key)
                ? "[REDACTED]"
                : parameter.Value;
        }

        return redacted;
    }

    private static bool IsPersonalDataParameter(SurrealQueryResult query, Type elementType, string parameterName)
    {
        var placeholder = "$" + parameterName;
        foreach (var where in query.Where)
        {
            if (!where.Contains(placeholder, StringComparison.Ordinal))
                continue;

            var fieldName = ExtractLeftHandField(where, placeholder);
            if (fieldName is null)
                continue;

            var property = elementType.GetProperty(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null)
                continue;

            return property.GetCustomAttributes(inherit: true)
                .Any(attribute =>
                {
                    var name = attribute.GetType().Name;
                    return string.Equals(name, "PersonalDataAttribute", StringComparison.Ordinal)
                        || string.Equals(name, "ProtectedPersonalDataAttribute", StringComparison.Ordinal);
                });
        }

        return false;
    }

    private static string? ExtractLeftHandField(string where, string placeholder)
    {
        var placeholderIndex = where.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex <= 0)
            return null;

        var beforePlaceholder = where[..placeholderIndex].TrimEnd();
        var operatorIndex = beforePlaceholder.LastIndexOfAny(['=', '<', '>', '!']);
        if (operatorIndex <= 0)
            return null;

        return beforePlaceholder[..operatorIndex]
            .Trim()
            .Trim('`');
    }

    private SurrealExpressionVisitor CreateVisitor()
        => new(_options.Schema, _options.EnumStorage);

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
    private void ExtractTable(Expression expression, SurrealQueryResult query)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
            query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
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
    /// Extracts the <see cref="SableQueryable{T}.ViewName"/> from the source
    /// queryable in the expression tree, if set.
    /// </summary>
    internal static string? ExtractViewName(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
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
    /// Extracts <see cref="SableQueryable{T}.FetchFields"/> from the source queryable
    /// embedded in the expression tree. Used to recover Fetch fields that were set on the
    /// original <see cref="SableQueryable{T}"/> before a LINQ operator (e.g.
    /// <c>.Where()</c>) created a new queryable via <c>CreateQuery</c>.
    /// </summary>
    internal static List<string>? ExtractFetchFields(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
            {
                var fetchFieldsField = qType.GetField("FetchFields",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var fields = fetchFieldsField?.GetValue(q) as List<string>;
                if (fields is { Count: > 0 })
                    return fields;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractFetchFields(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractFetchFields(u.Operand);
        return null;
    }

    /// <summary>
    /// Extracts <see cref="IncludeSpec"/> entries from the source queryable embedded
    /// in the expression tree. Used to recover IncludeSpecs that were set on the
    /// original <see cref="SableQueryable{T}"/> before a LINQ operator (e.g.
    /// <c>.Where()</c>) created a new queryable via <c>CreateQuery</c>.
    /// </summary>
    internal static List<IncludeSpec>? ExtractIncludeSpecs(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
            {
                var includeSpecsField = qType.GetField("IncludeSpecs",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var specs = includeSpecsField?.GetValue(q) as List<IncludeSpec>;
                if (specs is { Count: > 0 })
                    return specs;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractIncludeSpecs(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractIncludeSpecs(u.Operand);
        return null;
    }

    /// <summary>
    /// Extracts <see cref="FilterIncludeSpec"/> entries from the source queryable embedded
    /// in the expression tree. Used to recover FilterIncludeSpecs that were set on the
    /// original <see cref="SableQueryable{T}"/> before a LINQ operator (e.g.
    /// <c>.Where()</c>, <c>.OrderBy()</c>) created a new queryable via <c>CreateQuery</c>.
    /// </summary>
    internal static List<FilterIncludeSpec>? ExtractFilterIncludeSpecs(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
            {
                var filterSpecsField = qType.GetField("FilterIncludeSpecs",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var specs = filterSpecsField?.GetValue(q) as List<FilterIncludeSpec>;
                if (specs is { Count: > 0 })
                    return specs;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractFilterIncludeSpecs(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractFilterIncludeSpecs(u.Operand);
        return null;
    }

    internal static List<LinkedWhereSpec>? ExtractLinkedWhereSpecs(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
            {
                var linkedWhereField = qType.GetField("LinkedWhereSpecs",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var specs = linkedWhereField?.GetValue(q) as List<LinkedWhereSpec>;
                if (specs is { Count: > 0 })
                    return specs;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractLinkedWhereSpecs(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractLinkedWhereSpecs(u.Operand);
        return null;
    }

    /// <summary>
    /// Extracts <see cref="SableQueryable{T}.QueryStats"/> from the source queryable embedded
    /// in the expression tree. Used to recover the QueryStatistics reference set via
    /// <see cref="StatsExtensions.Stats{T}"/> when a LINQ operator (e.g. <c>.Where()</c>)
    /// created a new queryable via <c>CreateQuery</c>.
    /// </summary>
    internal static QueryStatistics? ExtractQueryStats(Expression expression)
    {
        if (expression is ConstantExpression c && c.Value is IQueryable q)
        {
            var qType = q.GetType();
            if (qType.IsGenericType && qType.GetGenericTypeDefinition() == typeof(SableQueryable<>))
            {
                var statsProp = qType.GetProperty("QueryStats",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return statsProp?.GetValue(q) as QueryStatistics;
            }
        }
        if (expression is MethodCallExpression m && m.Arguments.Count > 0)
            return ExtractQueryStats(m.Arguments[0]);
        if (expression is UnaryExpression u)
            return ExtractQueryStats(u.Operand);
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
            var tenantField = MetadataDispatch.GetFieldName(elementType, "TenantId", _options.Schema);
            query.Where.Add($"{tenantField} = '{_tenantId?.Replace("'", "\\'")}'");
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
            var deletedField = MetadataDispatch.GetFieldName(elementType, nameof(ISoftDeleted.Deleted), _options.Schema);
            query.Where.Add($"{deletedField} = false");
            _logger.LogDebug("Soft-delete filter applied to {Type}", elementType.Name);
        }
    }

    public IQueryable CreateQuery(Expression expression)
    {
        var elemType = expression.Type.GetGenericArguments().FirstOrDefault()
            ?? typeof(object);
        return (IQueryable)Activator.CreateInstance(
            typeof(SableQueryable<>).MakeGenericType(elemType),
            this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new SableQueryable<TElement>(this, expression);

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
        => await ToListAsyncInternal<T>(expression, null, null, null, null, null, ct);

    public async Task<T?> FirstOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
        => await FirstOrDefaultAsyncInternal<T>(expression, null, null, null, null, ct);

    public async Task<T?> SingleOrDefaultAsync<T>(Expression expression, CancellationToken ct = default)
        => await SingleOrDefaultAsyncInternal<T>(expression, null, null, null, null, ct);

    // --- Internal entry points (accept fetch/include from SableQueryable) ---

    internal async Task<List<T>> ToListAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SableQueryable<T>.IncludeDescriptor> includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        QueryStatistics? queryStats,
        CancellationToken ct = default)
        => await ToListAsyncInternal<T>(expression, fetchFields, includeDescriptors, includeSpecs, filterIncludeSpecs, queryStats, ct);

    internal async Task<T?> FirstOrDefaultAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SableQueryable<T>.IncludeDescriptor> includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        CancellationToken ct = default)
        => await FirstOrDefaultAsyncInternal<T>(expression, fetchFields, includeDescriptors, includeSpecs, filterIncludeSpecs, ct);

    internal async Task<T?> SingleOrDefaultAsync<T>(
        Expression expression,
        List<string> fetchFields,
        List<SableQueryable<T>.IncludeDescriptor> includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        CancellationToken ct = default)
        => await SingleOrDefaultAsyncInternal<T>(expression, fetchFields, includeDescriptors, includeSpecs, filterIncludeSpecs, ct);

    // --- Core implementation (shared between public and internal entry points) ---

    private async Task<List<T>> ToListAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SableQueryable<T>.IncludeDescriptor>? includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        QueryStatistics? queryStats,
        CancellationToken ct)
    {
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        var sourceType = ExtractElementType(expression) ?? typeof(T);
        ApplyLinkedWhereSpecs(query, expression, sourceType);
        ApplyTenantFilter(query, sourceType);
        ApplySoftDeleteFilter(query, sourceType);

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        // Propagate IncludeSpecs (inline subquery includes).
        // Also try to recover them from the expression tree (e.g. when created by .Where()).
        if (includeSpecs is { Count: > 0 })
            query.IncludeSpecs = includeSpecs;
        else
        {
            var extracted = ExtractIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
            {
                query.IncludeSpecs = extracted;
                includeSpecs = extracted;
            }
        }

        // Recover FilterIncludeSpecs from the expression tree — they are lost when
        // a standard LINQ operator (e.g. Where, OrderBy) creates a new SableQueryable.
        if (filterIncludeSpecs is not { Count: > 0 })
        {
            var extracted = ExtractFilterIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
                filterIncludeSpecs = extracted;
        }

        PromoteFetchFieldsToIncludeSpecs<T>(query, ref includeSpecs);

        var hasIncludes = includeDescriptors is { Count: > 0 };
        var hasIncludeSpecs = includeSpecs is { Count: > 0 };
        ThrowIfEncryptedIncludeOrFetch<T>(
            includeDescriptors, includeSpecs, query.FetchFields);

        // ── Stats: single-round-trip optimization ──────────────────────────────────
        // When no includes are present, prepend SELECT count() ... to the main query
        // and read both results from one RawQuery call. For queries with includes
        // (LET-based multi-statement), the result-set index detection is complex, so
        // the separate round-trip is kept with a TODO for future optimization.
        if (queryStats is not null)
        {
            if (!hasIncludes && !hasIncludeSpecs)
            {
                var countQuery = query.Clone();
                countQuery.OrderBy.Clear();
                countQuery.Limit = null;
                countQuery.Skip = null;
                countQuery.Projection = "count()";
                countQuery.GroupAll = true;
                var combinedSurql = countQuery.ToSurrealQL() + "\n" + query.ToSurrealQL();
                LogSurrealQuery("ToListAsync (with stats)", combinedSurql, query, sourceType);
                var statsSession = await GetSessionForElementType(sourceType, ct).ConfigureAwait(false);
                var statsResponse = await statsSession.RawQuery(combinedSurql, query.Parameters, ct).ConfigureAwait(false);
                if (!statsResponse.HasErrors && statsResponse.Count > 1)
                {
                    // Result set 0: count(), Result set 1: main data
                    var countVal = TryExtractCount(statsResponse);
                    if (countVal.HasValue)
                        queryStats.TotalResults = countVal.Value;
                    var dataResults = await DeserializeQueryResultsAsync<T>(statsResponse, 1, ct).ConfigureAwait(false);
                    await HydrateFetchedRecordLinksAsync(dataResults, query.FetchFields, statsSession, ct).ConfigureAwait(false);
                    if (filterIncludeSpecs is { Count: > 0 } && dataResults is { Count: > 0 })
                        dataResults = ApplyFilterIncludePredicates(dataResults, filterIncludeSpecs);
                    return dataResults;
                }
                return [];
            }
            else
            {
                // TODO: Combine count into the multi-statement LET query for single round-trip
                try
                {
                    queryStats.TotalResults = await CountAsync(expression, ct).ConfigureAwait(false);
                }
                catch { /* ignore count failures */ }
            }
        }

        string surql;
        if (hasIncludes || hasIncludeSpecs)
        {
            // Build multi-statement SurrealQL using LET variable for server-side
            // single-round-trip eager loading (analogous to Marten's temp tables).
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            // IncludeBatch descriptors (foreign key → callback/dictionary)
            if (hasIncludes)
            {
                foreach (var include in includeDescriptors!)
                {
                    var targetTable = MetadataDispatch.GetTableName(include.IncludeType, _options.Schema);
                    sb.Append("SELECT * FROM `")
                      .Append(targetTable)
                      .Append("` WHERE id IN (SELECT VALUE `")
                      .Append(include.FieldName)
                      .Append("` FROM $main);");
                }
            }

            // IncludeSpec forward/reverse includes (typed record<T> or collection)
            if (hasIncludeSpecs)
            {
                foreach (var spec in includeSpecs!)
                {
                    sb.Append("SELECT *, id AS __aerodb_include_id FROM `").Append(spec.TargetTable).Append("` WHERE ");
                    if (spec.IsForward)
                    {
                        // Forward: FK on parent. WHERE id IN (SELECT VALUE {parentFk} FROM $main)
                        sb.Append("id IN (SELECT VALUE `").Append(spec.ForeignKeyField).Append("`");
                    }
                    else
                    {
                        // Reverse: FK on child. WHERE {childFk} IN (SELECT VALUE {parentIdField} FROM $main)
                        // Use `Id` for Entity types (long FK), `id` for Record types (RecordId FK)
                        var idField = GetIdFieldForReverseInclude(spec);
                        sb.Append("`").Append(spec.ForeignKeyField).Append("` IN (SELECT VALUE ").Append(idField);
                    }
                    sb.Append(" FROM $main);");
                }
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        LogSurrealQuery("ToListAsync", surql, query, sourceType);
        var querySession = await GetSessionForElementType(sourceType, ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes || hasIncludeSpecs)
        {
            // Determine main result index once (shared between descriptor and spec processing).
            var includeResultCount = (includeDescriptors?.Count ?? 0) + (includeSpecs?.Count ?? 0);
            var mainIndex = ResolveMainResultIndex(response, includeResultCount);
            if (mainIndex < 0)
                return [];

            var results = DeserializeQueryResults<T>(response, mainIndex);
            if (results is null || results.Count == 0)
                return [];

            int ri = mainIndex + 1;

            if (hasIncludes)
            {
                foreach (var include in includeDescriptors!)
                {
                    if (ri >= response.Count) break;

                    if (response[ri] is not SurrealDbOkResult) { ri++; continue; }

                    object? includedListObj = DeserializeIncludeResultSet(response, ri, include.IncludeType);

                    if (includedListObj is not IEnumerable includedEnumerable) { ri++; continue; }

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

                    var fkProp = typeof(T).GetProperty(include.PropertyName, BindingFlags.Instance | BindingFlags.Public);
                    if (fkProp is null) { ri++; continue; }

                    foreach (var item in results)
                    {
                        var rawKey = fkProp.GetValue(item);
                        var strKey = ExtractKeyString(rawKey);
                        if (strKey is null || !docById.TryGetValue(strKey, out var matchedDoc))
                            continue;

                        if (include.Callback is not null)
                            include.Callback.DynamicInvoke(matchedDoc);
                        else if (include.Dictionary is not null)
                        {
                            var addMethod = include.Dictionary.GetType()
                                .GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
                            addMethod?.Invoke(include.Dictionary, [rawKey, matchedDoc]);
                        }
                    }

                    ri++;
                }
            }

            if (hasIncludeSpecs)
            {
                // Pass explicit offset (after any IncludeDescriptors result sets)
                var specOffset = hasIncludes ? ri : (int?)null;
                ApplyIncludeSpecsToResults(response, results, includeSpecs!, specOffset);
            }

            // Apply FilterInclude predicates (in-memory filter after includes are loaded)
            if (filterIncludeSpecs is { Count: > 0 } && results is { Count: > 0 })
                results = ApplyFilterIncludePredicates(results, filterIncludeSpecs);

            return results;
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var results = await DeserializeQueryResultsAsync<T>(response, 0, ct).ConfigureAwait(false);
            await HydrateFetchedRecordLinksAsync(results, query.FetchFields, querySession, ct).ConfigureAwait(false);
            return results;
        }

        return [];
    }

    private async Task<T?> FirstOrDefaultAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SableQueryable<T>.IncludeDescriptor>? includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        CancellationToken ct)
    {
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        var sourceType = ExtractElementType(expression) ?? typeof(T);
        ApplyTenantFilter(query, sourceType);
        ApplySoftDeleteFilter(query, sourceType);

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        // Propagate IncludeSpecs (inline subquery includes).
        if (includeSpecs is { Count: > 0 })
            query.IncludeSpecs = includeSpecs;
        else
        {
            var extracted = ExtractIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
            {
                query.IncludeSpecs = extracted;
                includeSpecs = extracted;
            }
        }

        // Recover FilterIncludeSpecs from the expression tree
        if (filterIncludeSpecs is not { Count: > 0 })
        {
            var extracted = ExtractFilterIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
                filterIncludeSpecs = extracted;
        }

        PromoteFetchFieldsToIncludeSpecs<T>(query, ref includeSpecs);

        query.Limit = 1;

        var hasIncludes = includeDescriptors is { Count: > 0 };
        var hasIncludeSpecs = includeSpecs is { Count: > 0 };
        ThrowIfEncryptedIncludeOrFetch<T>(
            includeDescriptors, includeSpecs, query.FetchFields);

        string surql;
        if (hasIncludes || hasIncludeSpecs)
        {
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            if (hasIncludes)
            {
                foreach (var include in includeDescriptors!)
                {
                    var targetTable = MetadataDispatch.GetTableName(include.IncludeType, _options.Schema);
                    sb.Append("SELECT * FROM `")
                      .Append(targetTable)
                      .Append("` WHERE id IN (SELECT VALUE `")
                      .Append(include.FieldName)
                      .Append("` FROM $main);");
                }
            }

            if (hasIncludeSpecs)
            {
                foreach (var spec in includeSpecs!)
                {
                    sb.Append("SELECT *, id AS __aerodb_include_id FROM `").Append(spec.TargetTable).Append("` WHERE ");
                    if (spec.IsForward)
                    {
                        sb.Append("id IN (SELECT VALUE `").Append(spec.ForeignKeyField).Append("`");
                    }
                    else
                    {
                        var idField = GetIdFieldForReverseInclude(spec);
                        sb.Append("`").Append(spec.ForeignKeyField).Append("` IN (SELECT VALUE ").Append(idField);
                    }
                    sb.Append(" FROM $main);");
                }
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        LogSurrealQuery("FirstOrDefaultAsync", surql, query, sourceType);
        var querySession = await GetSessionForElementType(sourceType, ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes)
        {
            var results = DeserializeMainAndIncludes(response, includeDescriptors!, ct);
            if (hasIncludeSpecs)
                ApplyIncludeSpecsToResults(response, results, includeSpecs!);
            if (filterIncludeSpecs is { Count: > 0 } && results is { Count: > 0 })
                results = ApplyFilterIncludePredicates(results, filterIncludeSpecs);
            if (results.Count > 0)
                return results[0];
            return default;
        }

        if (hasIncludeSpecs)
        {
            var results = DeserializeMainResults<T>(response);
            if (results.Count > 0)
            {
                ApplyIncludeSpecsToResults(response, results, includeSpecs!);
                if (filterIncludeSpecs is { Count: > 0 })
                    results = ApplyFilterIncludePredicates(results, filterIncludeSpecs);
                if (results.Count > 0)
                    return results[0];
            }
            return default;
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = await DeserializeQueryResultsAsync<T>(response, 0, ct).ConfigureAwait(false);
            if (raw is { Count: > 0 })
            {
                await HydrateFetchedRecordLinksAsync(raw, query.FetchFields, querySession, ct).ConfigureAwait(false);
                return raw[0];
            }
        }

        return default;
    }

    private async Task<T?> SingleOrDefaultAsyncInternal<T>(
        Expression expression,
        List<string>? fetchFields,
        List<SableQueryable<T>.IncludeDescriptor>? includeDescriptors,
        List<IncludeSpec>? includeSpecs,
        List<FilterIncludeSpec>? filterIncludeSpecs,
        CancellationToken ct)
    {
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        ExtractTable(expression, query);

        // Apply ViewName override if the source queryable has one
        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        var sourceType = ExtractElementType(expression) ?? typeof(T);
        ApplyTenantFilter(query, sourceType);
        ApplySoftDeleteFilter(query, sourceType);

        // Propagate Fetch fields
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        // Propagate IncludeSpecs (inline subquery includes).
        if (includeSpecs is { Count: > 0 })
            query.IncludeSpecs = includeSpecs;
        else
        {
            var extracted = ExtractIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
            {
                query.IncludeSpecs = extracted;
                includeSpecs = extracted;
            }
        }

        // Recover FilterIncludeSpecs from the expression tree
        if (filterIncludeSpecs is not { Count: > 0 })
        {
            var extracted = ExtractFilterIncludeSpecs(expression);
            if (extracted is { Count: > 0 })
                filterIncludeSpecs = extracted;
        }

        PromoteFetchFieldsToIncludeSpecs<T>(query, ref includeSpecs);

        query.Limit = 2; // fetch 2 to detect > 1 result

        var hasIncludes = includeDescriptors is { Count: > 0 };
        var hasIncludeSpecs = includeSpecs is { Count: > 0 };
        ThrowIfEncryptedIncludeOrFetch<T>(
            includeDescriptors, includeSpecs, query.FetchFields);

        string surql;
        if (hasIncludes || hasIncludeSpecs)
        {
            var baseSurql = query.ToSurrealQL().TrimEnd(';');
            var sb = new StringBuilder();
            sb.Append("LET $main = (");
            sb.Append(baseSurql);
            sb.AppendLine(");");
            sb.AppendLine("SELECT * FROM $main;");

            if (hasIncludes)
            {
                foreach (var include in includeDescriptors!)
                {
                    var targetTable = MetadataDispatch.GetTableName(include.IncludeType, _options.Schema);
                    sb.Append("SELECT * FROM `")
                      .Append(targetTable)
                      .Append("` WHERE id IN (SELECT VALUE `")
                      .Append(include.FieldName)
                      .Append("` FROM $main);");
                }
            }

            if (hasIncludeSpecs)
            {
                foreach (var spec in includeSpecs!)
                {
                    sb.Append("SELECT *, id AS __aerodb_include_id FROM `").Append(spec.TargetTable).Append("` WHERE ");
                    if (spec.IsForward)
                    {
                        sb.Append("id IN (SELECT VALUE `").Append(spec.ForeignKeyField).Append("`");
                    }
                    else
                    {
                        var idField = GetIdFieldForReverseInclude(spec);
                        sb.Append("`").Append(spec.ForeignKeyField).Append("` IN (SELECT VALUE ").Append(idField);
                    }
                    sb.Append(" FROM $main);");
                }
            }

            surql = sb.ToString();
        }
        else
        {
            surql = query.ToSurrealQL();
        }

        LogSurrealQuery("SingleOrDefaultAsync", surql, query, sourceType);
        var querySession = await GetSessionForElementType(sourceType, ct).ConfigureAwait(false);
        var response = await querySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (hasIncludes)
        {
            var results = DeserializeMainAndIncludes(response, includeDescriptors!, ct);
            if (hasIncludeSpecs)
                ApplyIncludeSpecsToResults(response, results, includeSpecs!);
            if (filterIncludeSpecs is { Count: > 0 } && results is { Count: > 0 })
                results = ApplyFilterIncludePredicates(results, filterIncludeSpecs);
            if (results.Count > 1)
                throw new InvalidOperationException("Sequence contains more than one element.");
            if (results.Count == 1)
                return results[0];
            return default;
        }

        if (hasIncludeSpecs)
        {
            var results = DeserializeMainResults<T>(response);
            if (results.Count > 0)
            {
                ApplyIncludeSpecsToResults(response, results, includeSpecs!);
                if (filterIncludeSpecs is { Count: > 0 })
                    results = ApplyFilterIncludePredicates(results, filterIncludeSpecs);
                if (results.Count > 1)
                    throw new InvalidOperationException("Sequence contains more than one element.");
                if (results.Count == 1)
                    return results[0];
            }
            return default;
        }

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = await DeserializeQueryResultsAsync<T>(response, 0, ct).ConfigureAwait(false);
            if (raw is { Count: > 0 })
            {
                await HydrateFetchedRecordLinksAsync(raw, query.FetchFields, querySession, ct).ConfigureAwait(false);
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
        List<SableQueryable<T>.IncludeDescriptor> includes,
        CancellationToken ct)
    {
        var includeCount = includes.Count;

        var mainIndex = ResolveMainResultIndex(response, includeCount);
        if (mainIndex < 0)
            return [];

        var results = DeserializeQueryResults<T>(response, mainIndex);
        if (results is null || results.Count == 0)
            return results ?? [];

        // Deserialize included docs starting after main results
        int resultIndex = mainIndex + 1;
        foreach (var include in includes)
        {
            if (resultIndex >= response.Count) break;

            if (response[resultIndex] is not SurrealDbOkResult)
            {
                resultIndex++;
                continue;
            }

            object? includedListObj = DeserializeIncludeResultSet(response, resultIndex, include.IncludeType);

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
    /// Deserializes main results from a multi-statement SurrealDbResponse,
    /// handling both engine behaviors (LET result present or absent).
    /// Used by IncludeSpec (forward include) post-processing.
    /// </summary>
    private static int ResolveMainResultIndex(SurrealDbResponse response, int includeResultCount)
    {
        if (response.Count <= 0)
            return -1;

        var expectedWithoutLet = includeResultCount + 1;
        var expectedWithLet = includeResultCount + 2;

        if (response.Count >= expectedWithLet)
            return response.Count - includeResultCount - 1;

        if (response.Count >= expectedWithoutLet)
            return response.Count - includeResultCount - 1;

        return response.Count > 1 ? 1 : 0;
    }

    private List<T> DeserializeMainResults<T>(SurrealDbResponse response)
    {
        var testMain = DeserializeQueryResults<T>(response, 0);
        if (testMain is { Count: > 0 })
            return testMain;

        if (response.Count > 1)
        {
            var altMain = DeserializeQueryResults<T>(response, 1);
            if (altMain is { Count: > 0 })
                return altMain;
        }

        return [];
    }

    /// <summary>
    /// Deserializes query results via source-generated shim types for IEntity{TId} entities,
    /// or directly for Record subclasses.
    /// </summary>
    private List<T> DeserializeQueryResults<T>(SurrealDbResponse response, int index)
    {
        // Use raw CBOR map reading for document classes so the active
        // schema naming policy is honored for convention-only Record/POCO types.
        var mapping = _options.Schema.Mappings.GetValueOrDefault(typeof(T));
        if (typeof(T).IsClass)
        {
            var items = CborResultReader.ReadPocoResult(response, index);
            if (items is { Count: > 0 })
                return InternalSessionBase.DeserializePocoFromList<T>(items, mapping?.IdentityProperty ?? "Id", _options.Schema);
        }

        var shimType = MetadataRegistry.GetShimType(typeof(T));
        if (shimType is not null)
        {
            var listType = typeof(List<>).MakeGenericType(shimType);
            var getValueMethod = typeof(SurrealDbResponse)
                .GetMethod(nameof(SurrealDbResponse.GetValue), 1, [typeof(int)])!
                .MakeGenericMethod(listType);

            var shimList = getValueMethod.Invoke(response, [index]);
            if (shimList is not IEnumerable enumerable)
                return [];

            // Materialize each shim to entity via ToEntity()
            var toEntityMethod = shimType.GetMethod("ToEntity", Type.EmptyTypes);
            if (toEntityMethod is null) return [];

            var results = new List<T>();
            foreach (var shim in enumerable)
            {
                if (shim is null) continue;
                var entity = toEntityMethod.Invoke(shim, null);
                if (entity is T t)
                    results.Add(t);
            }
            return results;
        }

        var raw = response.GetValue<List<T>>(index);
        return raw ?? [];
    }

    private async ValueTask<List<T>> DeserializeQueryResultsAsync<T>(
        SurrealDbResponse response,
        int index,
        CancellationToken cancellationToken)
    {
        if (!EncryptedFieldResolver.HasEncryptedFields(typeof(T), _options.Schema))
            return DeserializeQueryResults<T>(response, index);
        if (_sessionBase is null)
        {
            throw new SableEncryptedOperationNotSupportedException(
                typeof(T),
                "encrypted query without a Sable session materializer");
        }

        var mapping = _options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var records = CborResultReader.ReadPocoResult(response, index);
        return await _sessionBase
            .DeserializePocoFromListAsync<T>(
                records,
                mapping?.IdentityProperty ?? "Id",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ThrowIfEncryptedIncludeOrFetch<T>(
        IReadOnlyCollection<SableQueryable<T>.IncludeDescriptor>? includes,
        IReadOnlyCollection<IncludeSpec>? includeSpecs,
        IReadOnlyCollection<string> fetchFields)
    {
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), _options.Schema)
            && (includes is { Count: > 0 }
                || includeSpecs is { Count: > 0 }
                || fetchFields.Count > 0))
        {
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "include/fetch query");
        }

        var encryptedTarget = (includes?
                .Select(include => include.IncludeType) ?? [])
            .Concat(includeSpecs?.Select(include => include.IncludeType) ?? [])
            .FirstOrDefault(type =>
                EncryptedFieldResolver.HasEncryptedFields(type, _options.Schema));
        if (encryptedTarget is not null)
        {
            throw new SableEncryptedOperationNotSupportedException(
                encryptedTarget,
                "include/fetch target materialization");
        }
    }

    private void PromoteFetchFieldsToIncludeSpecs<T>(
        SurrealQueryResult query,
        ref List<IncludeSpec>? includeSpecs)
    {
        if (query.FetchFields.Count == 0)
            return;

        var promoted = new List<string>();
        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => typeof(IRecord).IsAssignableFrom(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .ToArray();

        foreach (var fetchField in query.FetchFields)
        {
            var property = properties.FirstOrDefault(p =>
                string.Equals(p.Name, fetchField, StringComparison.OrdinalIgnoreCase)
                || string.Equals(MetadataDispatch.GetFieldName(typeof(T), p.Name, _options.Schema), fetchField, StringComparison.OrdinalIgnoreCase));

            if (property is null)
                continue;

            var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            includeSpecs ??= [];
            if (includeSpecs.Any(spec => spec.IsForward && spec.PropertyName == property.Name))
            {
                promoted.Add(fetchField);
                continue;
            }

            includeSpecs.Add(new IncludeSpec
            {
                PropertyName = property.Name,
                ForeignKeyClrName = property.Name,
                ForeignKeyField = MetadataDispatch.GetFieldName(typeof(T), property.Name, _options.Schema),
                TargetTable = MetadataDispatch.GetTableName(targetType, _options.Schema),
                IncludeType = targetType,
                IsSingle = true,
                IsForward = true
            });
            promoted.Add(fetchField);
        }

        if (promoted.Count > 0)
            query.FetchFields.RemoveAll(field => promoted.Contains(field, StringComparer.OrdinalIgnoreCase));
    }

    private async Task HydrateFetchedRecordLinksAsync<T>(
        List<T> results,
        IReadOnlyList<string> fetchFields,
        ISurrealDbSession session,
        CancellationToken ct)
    {
        if (results.Count == 0 || fetchFields.Count == 0)
            return;

        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && typeof(IRecord).IsAssignableFrom(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .ToArray();

        if (properties.Length == 0)
            return;

        foreach (var fetchField in fetchFields)
        {
            var property = properties.FirstOrDefault(p =>
                string.Equals(p.Name, fetchField, StringComparison.OrdinalIgnoreCase)
                || string.Equals(MetadataDispatch.GetFieldName(typeof(T), p.Name, _options.Schema), fetchField, StringComparison.OrdinalIgnoreCase));

            if (property is null)
                continue;

            var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            foreach (var item in results)
            {
                if (item is null)
                    continue;

                if (property.GetValue(item) is not IRecord linked)
                    continue;

                var recordId = linked.Id
                    ?? await LoadFetchedRecordIdFromSourceAsync(item, fetchField, session, ct).ConfigureAwait(false);
                if (recordId is null)
                    continue;

                var hydrated = await LoadFetchedRecordAsync(targetType, recordId, session, ct).ConfigureAwait(false);
                if (hydrated is not null)
                    property.SetValue(item, hydrated);
            }
        }
    }

    private async Task<RecordId?> LoadFetchedRecordIdFromSourceAsync<T>(
        T source,
        string fetchField,
        ISurrealDbSession session,
        CancellationToken ct)
    {
        if (source is not IRecord sourceRecord || sourceRecord.Id is null)
            return null;

        var sourceLiteral = DocumentIdentityResolver.FormatRecordIdLiteral(sourceRecord.Id);
        var sql = $"SELECT `{fetchField}` AS link_id FROM {sourceLiteral}";
        var response = await session.RawQuery(sql, null, ct).ConfigureAwait(false);
        if (response.HasErrors || response.Count == 0)
            return null;

        var records = CborResultReader.ReadPocoResult(response, 0);
        if (records.Count == 0 || !records[0].TryGetValue("link_id", out var value))
            return null;

        if (value is RecordId recordId)
            return recordId;

        var recordText = value?.ToString();
        if (string.IsNullOrWhiteSpace(recordText) || !recordText.Contains(':', StringComparison.Ordinal))
            return null;

        var colon = recordText.IndexOf(':');
        return RecordId.From(recordText[..colon], recordText[(colon + 1)..]);
    }

    private Task<object?> LoadFetchedRecordAsync(Type targetType, RecordId recordId, ISurrealDbSession session, CancellationToken ct)
    {
        var method = GetType()
            .GetMethod(nameof(LoadFetchedRecordGenericAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(targetType);
        return (Task<object?>)method.Invoke(this, [recordId, session, ct])!;
    }

    private async Task<object?> LoadFetchedRecordGenericAsync<TFetched>(
        RecordId recordId,
        ISurrealDbSession session,
        CancellationToken ct)
        where TFetched : class
    {
        var sql = $"SELECT * FROM {DocumentIdentityResolver.FormatRecordIdLiteral(recordId)}";
        var response = await session.RawQuery(sql, null, ct).ConfigureAwait(false);
        if (response.HasErrors || response.Count == 0)
            return null;

        var records = CborResultReader.ReadPocoResult(response, 0);
        if (records.Count == 0)
            return null;

        var mapping = _options.Schema.Mappings.GetValueOrDefault(typeof(TFetched));
        return InternalSessionBase.DeserializePocoFromList<TFetched>(
            records,
            mapping?.IdentityProperty ?? "Id",
            _options.Schema).FirstOrDefault();
    }

    private static string? StripRecordTable(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key;

        var colon = key.LastIndexOf(':');
        return colon >= 0 && colon < key.Length - 1 ? key[(colon + 1)..] : key;
    }

    /// <summary>
    /// Applies IncludeSpec results to main results by matching the FK RecordId
    /// on each main result with the loaded include document's Id.
    /// Sets the typed property directly on each main result.
    /// </summary>
    /// <param name="response">The SurrealDB response containing main + include result sets.</param>
    /// <param name="results">Already-deserialized main results.</param>
    /// <param name="includeSpecs">The include specifications to apply.</param>
    /// <param name="resultOffset">
    /// Optional offset into the response for the first include result set.
    /// Defaults to <c>mainIndex + 1</c> (no prior include descriptor sets).
    /// </param>
    private void ApplyIncludeSpecsToResults<T>(
        SurrealDbResponse response,
        List<T> results,
        List<IncludeSpec> includeSpecs,
        int? resultOffset = null)
    {
        int resultIndex;
        if (resultOffset.HasValue)
        {
            resultIndex = resultOffset.Value;
        }
        else
        {
            // Determine main result index
            int mainIndex;
            var testMain = DeserializeQueryResults<T>(response, 0);
            if (testMain is { Count: > 0 })
                mainIndex = 0;
            else if (response.Count > 1)
                mainIndex = 1;
            else
                return;
            resultIndex = mainIndex + 1;
        }
        foreach (var spec in includeSpecs)
        {
            if (resultIndex >= response.Count) break;

            if (response[resultIndex] is not SurrealDbOkResult)
            {
                resultIndex++;
                continue;
            }

            object? includedListObj = DeserializeIncludeResultSet(response, resultIndex, spec.IncludeType);

            if (includedListObj is not IEnumerable includedEnumerable)
            {
                resultIndex++;
                continue;
            }

            if (!spec.IsForward)
            {
                // ── Reverse include: group by FK field on child ────────
                var fkProp = spec.IncludeType.GetProperty(spec.ForeignKeyClrName,
                    BindingFlags.Instance | BindingFlags.Public);

                if (fkProp is null) { resultIndex++; continue; }

                var groups = new Dictionary<string, List<object>>(StringComparer.Ordinal);
                foreach (var typedDoc in includedEnumerable)
                {
                    if (typedDoc is null) continue;
                    var fkValue = ExtractKeyString(fkProp.GetValue(typedDoc));
                    if (fkValue is null) continue;
                    if (!groups.ContainsKey(fkValue))
                        groups[fkValue] = new List<object>();
                    groups[fkValue].Add(typedDoc);
                }

                // Assign grouped results to each main result
                var idProp = typeof(T).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                var includeProp = typeof(T).GetProperty(spec.PropertyName, BindingFlags.Instance | BindingFlags.Public);
                if (includeProp is null) { resultIndex++; continue; }

                foreach (var item in results)
                {
                    if (item is null) continue;
                    var parentId = ExtractKeyString(idProp?.GetValue(item));
                    if (parentId is null) continue;

                    if (groups.TryGetValue(parentId, out var children))
                    {
                        // Create typed list and set on property
                        var childListType = typeof(List<>).MakeGenericType(spec.IncludeType);
                        var typedList = (IList)Activator.CreateInstance(childListType)!;
                        foreach (var child in children)
                            typedList.Add(child);

                        includeProp.SetValue(item, typedList);
                    }
                }

                resultIndex++;
                continue;
            }

            // ── Forward include: build lookup by Id ───────────────────
            var idPropFwd = spec.IncludeType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
            var docById = new Dictionary<string, object?>(StringComparer.Ordinal);
            var includeRecords = CborResultReader.ReadPocoResult(response, resultIndex);
            var includeRecordIndex = 0;
            foreach (var typedDoc in includedEnumerable)
            {
                if (typedDoc is null) continue;
                var docId = idPropFwd?.GetValue(typedDoc);
                var strKey = ExtractKeyString(docId);
                if (strKey is null
                    && includeRecordIndex < includeRecords.Count
                    && includeRecords[includeRecordIndex].TryGetValue("__aerodb_include_id", out var aliasedId))
                {
                    strKey = StripRecordTable(ExtractKeyString(aliasedId));
                }

                if (strKey is not null)
                {
                    if (!docById.ContainsKey(strKey))
                        docById[strKey] = typedDoc;

                    var strippedKey = StripRecordTable(strKey);
                    if (!string.IsNullOrWhiteSpace(strippedKey) && !docById.ContainsKey(strippedKey))
                        docById[strippedKey] = typedDoc;
                }

                includeRecordIndex++;
            }

            if (docById.Count == 0)
            {
                resultIndex++;
                continue;
            }

            // For each source result, extract the FK from its typed record property,
            // look up the matching include document, and set the property.
            var includePropFwd = typeof(T).GetProperty(spec.PropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (includePropFwd is null) { resultIndex++; continue; }

            foreach (var item in results)
            {
                if (item is null) continue;

                // The property holds a Record reference (e.g., Customer?) which was
                // deserialized with just the Id populated (from the stored RecordId).
                var propValue = includePropFwd.GetValue(item);
                if (propValue is null) continue;

                // Get the Id from the record reference
                var recordId = idPropFwd?.GetValue(propValue);
                var strKey = ExtractKeyString(recordId);
                if (strKey is null || !docById.TryGetValue(strKey, out var matchedDoc))
                    continue;

                // Set the full included document on the property
                includePropFwd.SetValue(item, matchedDoc);
            }

            resultIndex++;
        }
    }

    private object? DeserializeIncludeResultSet(SurrealDbResponse response, int index, Type includeType)
    {
        var method = typeof(SurrealQueryProvider)
            .GetMethod(nameof(DeserializeIncludeResultSetGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(includeType);
        return method.Invoke(this, [response, index]);
    }

    private List<TInclude> DeserializeIncludeResultSetGeneric<TInclude>(SurrealDbResponse response, int index)
    {
        var mapping = _options.Schema.Mappings.GetValueOrDefault(typeof(TInclude));
        var records = CborResultReader.ReadPocoResult(response, index);
        return InternalSessionBase.DeserializePocoFromList<TInclude>(records, mapping?.IdentityProperty ?? "Id", _options.Schema);
    }

    /// <summary>
    /// Applies FilterInclude predicates in-memory after all includes are loaded.
    /// For each filter spec, evaluates the predicate against the child collection
    /// and removes parent documents that don't match.
    /// </summary>
    private static List<T> ApplyFilterIncludePredicates<T>(
        List<T> results,
        List<FilterIncludeSpec> filterIncludeSpecs)
    {
        foreach (var spec in filterIncludeSpecs)
        {
            var compiledFilter = spec.Filter.Compile();
            results = results.Where(item =>
            {
                var prop = typeof(T).GetProperty(spec.PropertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (prop is null) return false;

                var collection = prop.GetValue(item);
                if (collection is null) return false;

                try
                {
                    return (bool)compiledFilter.DynamicInvoke(collection)!;
                }
                catch
                {
                    return false;
                }
            }).ToList();
        }

        return results;
    }

    /// <summary>
    /// Extracts a string key from a value for dictionary-based Include matching.
    /// Handles RecordId, string, Guid, and primitive types.
    /// </summary>
    /// <summary>
    /// Returns the SurrealDB field name for the parent's ID in a reverse-include subquery.
    /// Uses the pre-computed value from <see cref="IncludeSpec.ParentIdField"/>.
    /// </summary>
    private static string GetIdFieldForReverseInclude(IncludeSpec spec)
    {
        // Validate FK field exists on child type
        var fkProp = spec.IncludeType.GetProperty(
            spec.ForeignKeyClrName,
            BindingFlags.Public | BindingFlags.Instance);

        if (fkProp is null && !string.IsNullOrWhiteSpace(spec.ForeignKeyClrName))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AeroDB.Sable] IncludeReverse: FK field '{spec.ForeignKeyClrName}' not found on type '{spec.IncludeType.Name}'. " +
                "Reverse include results may be incorrect.");
        }

        return spec.ParentIdField;
    }

    private static string? ExtractKeyString(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        if (value is Guid g) return g.ToString();
        if (value is RecordIdOf<string> sRid) return sRid.Id;
        if (value is RecordIdOf<long> lRid) return lRid.Id.ToString();
        if (value is RecordIdOf<int> iRid) return iRid.Id.ToString();
        var valueType = value.GetType();
        if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
        {
            var idValue = valueType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
            return idValue switch
            {
                null => null,
                Guid guid => guid.ToString(),
                _ => idValue.ToString()
            };
        }
        if (value is RecordId rid)
        {
            try { return rid.DeserializeId<string>(); } catch { }
            try { return rid.DeserializeId<long>().ToString(); } catch { }
            try { return rid.DeserializeId<int>().ToString(); } catch { }
            try { return rid.DeserializeId<Guid>().ToString(); } catch { }
            return rid.Table;
        }
        return value.ToString();
    }

    public async Task<decimal> AggregateAsync<T>(Expression expression, string fieldName, string function, CancellationToken ct = default)
    {
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
            ExtractTable(expression, query);
        if (viewName is not null)
            query.TableName = viewName;

        var elementType = ExtractElementType(expression) ?? typeof(T);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);

        query.OrderBy.Clear();
        query.Limit = null;
        query.Skip = null;
        query.GroupAll = true;

        query.Projection = $"{function}({fieldName})";
        var surql = query.ToSurrealQL();
        LogSurrealQuery($"AggregateAsync ({function})", surql, query, elementType);
        var aggSession = await GetSessionForElementType(elementType, ct).ConfigureAwait(false);
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
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }
        if (viewName is not null)
            query.TableName = viewName;

        var elementType = ExtractElementType(expression);
        ApplyLinkedWhereSpecs(query, expression, elementType);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);

        // Strip ordering and limit — they don't affect count
        query.OrderBy.Clear();
        query.Limit = null;
        query.Skip = null;
        query.Projection = "count()";
        query.GroupAll = true;

        var surql = query.ToSurrealQL();
        LogSurrealQuery("CountAsync", surql, query, elementType);
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
        EncryptedQueryGuard.Validate(sourceExpression, _options.Schema);
        var visitor = new SurrealExpressionVisitor(null, _options.EnumStorage);
        var viewName = ExtractViewName(sourceExpression);
        visitor.ViewName = viewName;
        var result = visitor.Translate(sourceExpression);
        if (string.IsNullOrEmpty(result.TableName))
        {
            if (sourceExpression is ConstantExpression c && c.Value is IQueryable q)
                result.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }
        if (viewName is not null)
            result.TableName = viewName;

        var elementType = ExtractElementType(sourceExpression) ?? typeof(T);
        ApplyTenantFilter(result, elementType);
        ApplySoftDeleteFilter(result, elementType);

        // Override SELECT + GROUP BY with aggregate builder's values
        result.Projection = builder.BuildSelect();
        if (builder.GroupByClause is not null)
            result.GroupBy = builder.GroupByClause.Split(", ").ToList();

        var surql = result.ToSurrealQL();
        var querySession = await GetSessionForElementType(elementType, ct).ConfigureAwait(false);
        LogSurrealQuery("SelectAsync", surql, result, elementType);
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
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var viewName = ExtractViewName(expression);
        visitor.ViewName = viewName;
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }
        if (viewName is not null)
            query.TableName = viewName;

        var elementType = ExtractElementType(expression);
        ApplyLinkedWhereSpecs(query, expression, elementType);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);
        query.Limit = 1;
        var surql = query.ToSurrealQL();
        LogSurrealQuery("AnyAsync", surql, query, elementType);
        var anySession = await GetSessionForElementType(elementType ?? typeof(object), ct).ConfigureAwait(false);
        var response = await anySession.RawQuery(surql, query.Parameters, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<object>>(0);
            return raw is { Count: > 0 };
        }

        return false;
    }

    /// <summary>
    /// Returns the generated SurrealQL for the given expression without executing it.
    /// Applies the same translation, tenant filtering, soft-delete filtering, view name
    /// overrides, and Fetch field propagation that would happen during execution.
    /// Useful for debugging and logging.
    /// </summary>
    public SableCommand ToCommand(Expression expression)
    {
        EncryptedQueryGuard.Validate(expression, _options.Schema);
        var visitor = CreateVisitor();
        var query = visitor.Translate(expression);
        if (string.IsNullOrEmpty(query.TableName))
        {
            if (expression is ConstantExpression c && c.Value is IQueryable q)
                query.TableName = MetadataDispatch.GetTableName(q.ElementType, _options.Schema);
        }

        var viewName = ExtractViewName(expression);
        if (viewName is not null)
            query.TableName = viewName;

        var elementType = ExtractElementType(expression);
        ApplyLinkedWhereSpecs(query, expression, elementType);
        ApplyTenantFilter(query, elementType);
        ApplySoftDeleteFilter(query, elementType);

        // Recover FetchFields from the expression tree
        var fetchFields = ExtractFetchFields(expression);
        if (fetchFields is { Count: > 0 })
            query.FetchFields.AddRange(fetchFields);

        return new SableCommand(query.ToSurrealQL(), query.Parameters);
    }

    private void ApplyLinkedWhereSpecs(SurrealQueryResult query, Expression expression, Type? sourceType)
    {
        var specs = ExtractLinkedWhereSpecs(expression);
        if (specs is not { Count: > 0 })
            return;

        foreach (var spec in specs)
        {
            var visitor = CreateVisitor();
            query.Where.Add(visitor.TranslateLinkedWhere(spec.Predicate, spec.Links));
            if (visitor.Parameters.Count > 0)
            {
                var merged = new Dictionary<string, object?>(query.Parameters);
                foreach (var parameter in visitor.Parameters)
                    merged[parameter.Key] = parameter.Value;
                query.Parameters = merged;
            }
        }
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
            using var doc = JsonDocument.Parse(json!);
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
            using var doc = JsonDocument.Parse(json!);
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
