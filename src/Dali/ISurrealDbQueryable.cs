using System.Collections;
using System.Linq.Expressions;

namespace Dali;

public interface ISurrealDbQueryable<T> : IOrderedQueryable<T>
{
    Task<List<T>> ToListAsync(CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<T?> SingleOrDefaultAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> MinAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> MaxAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
    Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default);
}

public class SurrealDbQueryable<T> : ISurrealDbQueryable<T>, IAsyncEnumerable<T>, IOrderedQueryable
{
    private readonly SurrealQueryProvider _provider;

    public SurrealDbQueryable(SurrealQueryProvider provider)
    {
        _provider = provider;
        Expression = Expression.Constant(this);
        ElementType = typeof(T);
    }

    public SurrealDbQueryable(SurrealQueryProvider provider, Expression expression)
    {
        _provider = provider;
        Expression = expression;
        ElementType = typeof(T);
    }

    public Type ElementType { get; }
    public Expression Expression { get; }
    public IQueryProvider Provider => _provider;

    public IEnumerator<T> GetEnumerator()
        => _provider.ToListAsync<T>(Expression).GetAwaiter().GetResult().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        foreach (var item in await _provider.ToListAsync<T>(Expression, ct))
            yield return item;
    }

    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => _provider.ToListAsync<T>(Expression, ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _provider.FirstOrDefaultAsync<T>(Expression, ct);

    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var whereExpr = Expression.Call(
            typeof(Queryable), "Where", [typeof(T)],
            Expression, Expression.Quote(predicate));
        return _provider.FirstOrDefaultAsync<T>(whereExpr, ct);
    }

    public Task<T?> SingleOrDefaultAsync(CancellationToken ct = default)
        => _provider.SingleOrDefaultAsync<T>(Expression, ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => _provider.CountAsync(Expression, ct);

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _provider.AnyAsync(Expression, ct);

    public Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var sumExpr = Expression.Call(
            typeof(Queryable), "Sum", [typeof(T)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(sumExpr, fieldName, "math::sum", ct);
    }

    public Task<decimal> MinAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var minExpr = Expression.Call(
            typeof(Queryable), "Min", [typeof(T), typeof(decimal)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(minExpr, fieldName, "math::min", ct);
    }

    public Task<decimal> MaxAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var maxExpr = Expression.Call(
            typeof(Queryable), "Max", [typeof(T), typeof(decimal)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(maxExpr, fieldName, "math::max", ct);
    }

    public Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default)
    {
        var fieldName = ExtractFieldName(selector);
        var avgExpr = Expression.Call(
            typeof(Queryable), "Average", [typeof(T)],
            Expression, Expression.Quote(selector));
        return _provider.AggregateAsync<T>(avgExpr, fieldName, "math::mean", ct);
    }

    private static string ExtractFieldName<TDelegate>(Expression<TDelegate> selector)
    {
        if (selector.Body is MemberExpression m)
            return m.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression (e.g., p => p.Price)");
    }
}
