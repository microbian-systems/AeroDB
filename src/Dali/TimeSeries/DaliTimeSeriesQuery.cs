using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Dali.Metadata;

namespace Dali;

public sealed class DaliTimeSeriesQuery<T> : ITimeSeriesQuery<T> where T : class
{
    private readonly SurrealQueryProvider _provider;
    private readonly string _table;

    // Bucketing state
    private string? _bucketField;
    private string? _bucketExpr; // either "time::floor(field, 1d)" or "time::group(field, 'month')"

    // Aggregate state
    private string? _selectClause;
    private Action<AggregateQueryBuilder<T>>? _selectConfig;

    // Filter / pagination
    private Expression<Func<T, bool>>? _wherePredicate;
    private string? _whereClause;
    private int _limit = 1000;
    private int _skip;

    // Downsampling state
    private DateTime? _downsampleFrom;
    private DateTime? _downsampleTo;
    private int _targetBucketCount;

    private readonly SurrealCommandBuilder _paramBuilder = new();

    internal DaliTimeSeriesQuery(SurrealQueryProvider provider)
    {
        _provider = provider;
        _table = MetadataDispatch.GetTableName(typeof(T));
    }

    public ITimeSeriesQuery<T> BucketByFloor(Expression<Func<T, object>> timestampField, int value, TimeUnit unit)
    {
        _bucketField = GetMemberName(timestampField);
        var duration = TimeBucketHelper.ToFloorDuration(value, unit);
        _bucketExpr = $"time::floor({_bucketField}, {duration})";
        return this;
    }

    public ITimeSeriesQuery<T> BucketByGroup(Expression<Func<T, object>> timestampField, TimeBucket bucket)
    {
        _bucketField = GetMemberName(timestampField);
        var groupArg = TimeBucketHelper.ToGroupString(bucket);
        _bucketExpr = $"time::group({_bucketField}, '{groupArg}')";
        return this;
    }

    public ITimeSeriesQuery<T> Downsample(Expression<Func<T, object>> timestampField, int targetBucketCount)
    {
        _bucketField = GetMemberName(timestampField);
        _targetBucketCount = targetBucketCount;
        // Bucket width computed in BuildSurql() once time range is known
        return this;
    }

    public ITimeSeriesQuery<T> Select(Action<AggregateQueryBuilder<T>> configure)
    {
        _selectConfig = configure;
        return this;
    }

    public ITimeSeriesQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _wherePredicate = predicate;
        _whereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body, _paramBuilder);

        // Try to extract time range for downsampling
        if (_downsampleFrom is null && predicate.Body is BinaryExpression bin)
        {
            if (bin.Left is MemberExpression mem && bin.Right is ConstantExpression val)
            {
                if (val.Value is DateTime dt)
                {
                    if (bin.NodeType == ExpressionType.GreaterThanOrEqual)
                        _downsampleFrom = dt;
                    else if (bin.NodeType == ExpressionType.LessThanOrEqual)
                        _downsampleTo = dt;
                }
            }
        }
        return this;
    }

    public ITimeSeriesQuery<T> Take(int limit)
    {
        _limit = limit;
        return this;
    }

    public ITimeSeriesQuery<T> Skip(int count)
    {
        _skip = count;
        return this;
    }

    public async Task<List<T>> ToListAsync(CancellationToken ct = default)
    {
        var surql = BuildSurql();
        var response = await _provider.Session.RawQuery(surql, _paramBuilder.Parameters, ct).ConfigureAwait(false);
        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null) return raw;
        }
        return [];
    }

    private string BuildSurql()
    {
        var sb = new StringBuilder();

        // Resolve downsampling if needed
        if (_targetBucketCount > 0 && _bucketField is not null)
        {
            if (_downsampleFrom.HasValue && _downsampleTo.HasValue)
            {
                var (value, unit, duration) = TimeBucketHelper.ComputeBuckets(
                    _downsampleFrom.Value, _downsampleTo.Value, _targetBucketCount);
                _bucketExpr = $"time::floor({_bucketField}, {duration})";
            }
            else
            {
                // No time range available — use rough default
                _bucketExpr = $"time::floor({_bucketField}, 1d)";
            }
        }

        // Build select clause
        if (_selectConfig is not null)
        {
            var aggBuilder = new AggregateQueryBuilder<T>();
            _selectConfig(aggBuilder);
            _selectClause = aggBuilder.BuildSelect();
        }
        else
        {
            _selectClause = "count() AS cnt";
        }

        sb.Append($"SELECT {_selectClause}");

        // Add bucket expression if present
        if (_bucketExpr is not null)
        {
            sb.Append($", {_bucketExpr} AS _bucket");
        }

        sb.Append($" FROM `{_table}`");

        // WHERE clause
        var whereParts = new List<string>();
        if (_whereClause is not null)
            whereParts.Add($"({_whereClause})");

        if (whereParts.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", whereParts));

        // GROUP BY
        if (_bucketExpr is not null)
            sb.Append($" GROUP BY _bucket");

        // ORDER BY
        if (_bucketExpr is not null)
            sb.Append(" ORDER BY _bucket ASC");

        // LIMIT / SKIP
        sb.Append($" LIMIT {_limit}");
        if (_skip > 0)
            sb.Append($" START AT {_skip}");

        sb.Append(';');
        return sb.ToString();
    }

    private static string GetMemberName(Expression<Func<T, object>> selector)
    {
        if (selector.Body is MemberExpression m)
            return m.Member.Name;
        if (selector.Body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: MemberExpression um })
            return um.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression");
    }
}
