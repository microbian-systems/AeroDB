using System.Collections;
using System.Linq.Expressions;

namespace Dali;

public interface ISurrealDbQueryable<T> : IQueryable<T>
{
    Task<List<T>> ToListAsync(CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
}

public class SurrealDbQueryable<T> : ISurrealDbQueryable<T>, IAsyncEnumerable<T>
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

    public Task<int> CountAsync(CancellationToken ct = default)
        => _provider.CountAsync(Expression, ct);

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => _provider.AnyAsync(Expression, ct);
}
