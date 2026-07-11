using System.Linq.Expressions;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

/// <summary>
/// Fluent builder for ad-hoc aggregate queries against SurrealDB views and tables.
/// Supports count/sum/min/max/average, field references, raw expressions, and GROUP BY.
/// Use via <c>session.Query&lt;T&gt;().AggregateQuery(b => ...)</c>.
/// </summary>
public class AggregateQueryBuilder<T> where T : class
{
    private readonly List<(string Expression, string? Alias)> _columns = new();
    private bool _pendingAlias;

    /// <summary>GROUP BY column names for the aggregate query.</summary>
    public string? GroupByClause { get; private set; }

    /// <summary>Adds <c>count()</c> aggregate.</summary>
    public AggregateQueryBuilder<T> Count()
    {
        _columns.Add(("count()", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::sum(field)</c> aggregate.</summary>
    public AggregateQueryBuilder<T> Sum(Expression<Func<T, object?>> field)
    {
        _columns.Add(($"math::sum({GetFieldName(field)})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::min(field)</c> aggregate.</summary>
    public AggregateQueryBuilder<T> Min(Expression<Func<T, object?>> field)
    {
        _columns.Add(($"math::min({GetFieldName(field)})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::max(field)</c> aggregate.</summary>
    public AggregateQueryBuilder<T> Max(Expression<Func<T, object?>> field)
    {
        _columns.Add(($"math::max({GetFieldName(field)})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::mean(field)</c> aggregate.</summary>
    public AggregateQueryBuilder<T> Average(Expression<Func<T, object?>> field)
    {
        _columns.Add(($"math::mean({GetFieldName(field)})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds a bare field reference (no aggregation).</summary>
    public AggregateQueryBuilder<T> Field(Expression<Func<T, object?>> field)
    {
        _columns.Add((GetFieldName(field), null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Escape hatch: adds a raw SurrealQL expression.</summary>
    public AggregateQueryBuilder<T> Raw(string expression)
    {
        _columns.Add((expression, null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>
    /// Sets the alias for the most recently added expression.
    /// Throws <see cref="InvalidOperationException"/> if no pending expression exists.
    /// </summary>
    public AggregateQueryBuilder<T> As(string alias)
    {
        if (!_pendingAlias || _columns.Count == 0)
            throw new InvalidOperationException(
                "No pending expression to alias. Call .As() after .Count(), .Sum(), .Field(), etc.");
        var last = _columns[^1];
        _columns[^1] = (last.Expression, alias);
        _pendingAlias = false;
        return this;
    }

    /// <summary>Adds GROUP BY columns from a key selector expression.</summary>
    public AggregateQueryBuilder<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var columns = SurrealExpressionVisitor.ExtractGroupByColumns(keySelector);
        GroupByClause = string.Join(", ", columns);
        return this;
    }

    /// <summary>Builds the comma-separated SELECT column string.</summary>
    internal string BuildSelect()
    {
        if (_columns.Count == 0) return "*";
        return string.Join(", ", _columns.Select(c =>
            c.Alias is not null ? $"{c.Expression} AS {c.Alias}" : c.Expression));
    }

    private static string GetFieldName(Expression<Func<T, object?>> selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression m)
            return MetadataDispatch.GetFieldName(typeof(T), m.Member.Name, null);
        throw new ArgumentException("Selector must be a simple member expression");
    }
}
