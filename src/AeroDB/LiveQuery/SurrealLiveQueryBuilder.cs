using System.Linq.Expressions;
using AeroDB.Metadata;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;
using SurrealDb.Net.Models.LiveQuery;

namespace AeroDB.LiveQuery;

internal sealed class SurrealLiveQueryBuilder<T> : IDaliLiveQueryBuilder<T> where T : class
{
    private readonly ISurrealDbSession _session;
    private readonly StoreOptions _options;
    private readonly string? _tenantId;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SurrealLiveQueryBuilder<T>> _logger;
    private readonly List<Expression<Func<T, bool>>> _predicates = new();
    private readonly List<Expression<Func<T, object>>> _fields = new();
    private readonly List<Action<T>> _onCreated = new();
    private readonly List<Action<T>> _onUpdated = new();
    private readonly List<Action<T>> _onDeleted = new();
    private readonly List<Action> _onOpen = new();
    private int _channelCapacity;

    public SurrealLiveQueryBuilder(
        ISurrealDbSession session,
        StoreOptions options,
        ILoggerFactory loggerFactory,
        string? tenantId = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _tenantId = tenantId;
        _channelCapacity = options.LiveQueryChannelCapacity;
        _logger = loggerFactory.CreateLogger<SurrealLiveQueryBuilder<T>>();
    }

    public IDaliLiveQueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(predicate);
        return this;
    }

    public IDaliLiveQueryBuilder<T> Select(params Expression<Func<T, object>>[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields.AddRange(fields);
        return this;
    }

    /// <summary>
    /// M2: Override the per-subscription channel capacity (default: <see cref="StoreOptions.LiveQueryChannelCapacity"/>).
    /// </summary>
    public IDaliLiveQueryBuilder<T> ChannelCapacity(int capacity)
    {
        _channelCapacity = capacity;
        return this;
    }

    public IDaliLiveQueryBuilder<T> OnCreated(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onCreated.Add(handler);
        return this;
    }

    public IDaliLiveQueryBuilder<T> OnUpdated(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onUpdated.Add(handler);
        return this;
    }

    public IDaliLiveQueryBuilder<T> OnDeleted(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onDeleted.Add(handler);
        return this;
    }

    /// <summary>
    /// M3: Register a callback for when the live query opens (WebSocket connection established).
    /// </summary>
    public IDaliLiveQueryBuilder<T> OnOpen(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onOpen.Add(handler);
        return this;
    }

    public async Task<IDaliLiveQuery<T>> SubscribeAsync(CancellationToken ct = default)
    {
        var table = MetadataDispatch.GetTableName(typeof(T));
        var needsTenancyFilter = _options.TenancyStyle == TenancyStyle.Conjoined
                              && _tenantId is not null
                              && MetadataDispatch.HasTenantId(typeof(T));

        SurrealDbLiveQuery<T> sdkLive;
        if (_predicates.Count == 0 && _fields.Count == 0 && !needsTenancyFilter)
        {
            sdkLive = await _session.LiveTable<T>(table, diff: false, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var sql = BuildSurrealQL(table, needsTenancyFilter);
            sdkLive = await _session.LiveRawQuery<T>(sql, null, ct)
                .ConfigureAwait(false);
        }

        var queryLogger = _loggerFactory.CreateLogger<SurrealDaliLiveQuery<T>>();
        var query = new SurrealDaliLiveQuery<T>(
            sdkLive,
            _onCreated,
            _onUpdated,
            _onDeleted,
            _onOpen,
            _channelCapacity,
            _options.LiveQueryChannelFullMode,
            queryLogger);

        await query.StartAsync(ct).ConfigureAwait(false);
        return query;
    }

    private string BuildSurrealQL(string table, bool injectTenantFilter = false)
    {
        var fields = _fields.Count > 0
            ? string.Join(", ", _fields.Select(TranslateField))
            : "*";

        var parts = new List<string>();
        if (_predicates.Count > 0)
            parts.AddRange(_predicates
                .Select(p => SurrealExpressionVisitor.TranslateCondition(p.Body)));

        // H6 FIXED: auto-inject tenant_id filter for Conjoined tenancy
        if (injectTenantFilter)
            parts.Add($"tenant_id = '{_tenantId!.Replace("'", "\\'")}'");

        var conditions = parts.Count > 0
            ? " WHERE " + string.Join(" AND ", parts)
            : "";

        return $"LIVE SELECT {fields} FROM `{table}`{conditions}";
    }

    private static string TranslateField(Expression<Func<T, object>> expr)
    {
        if (expr.Body is MemberExpression m)
            return MetadataDispatch.ToSnakeCase(m.Member.Name);
        if (expr.Body is UnaryExpression { Operand: MemberExpression u })
            return MetadataDispatch.ToSnakeCase(u.Member.Name);
        return "*";
    }
}
