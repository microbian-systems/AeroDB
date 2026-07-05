using System.Linq.Expressions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;

namespace AeroDB;

public static class AggregateQueryExtensions
{
    /// <summary>
    /// Starts an ad-hoc aggregate query builder. The source queryable provides
    /// the table name and any WHERE/ORDER BY/LIMIT clauses from the LINQ chain.
    /// The aggregate builder overrides the SELECT and GROUP BY.
    /// </summary>
    public static AggregateQueryExecutor<T> AggregateQuery<T>(
        this IQueryable<T> source,
        Func<AggregateQueryBuilder<T>, AggregateQueryBuilder<T>> build)
        where T : class
    {
        if (source.Provider is not SurrealQueryProvider provider)
            throw new NotSupportedException("AggregateQuery requires AeroDB's SurrealQueryProvider.");

        var builder = new AggregateQueryBuilder<T>();
        build(builder);
        return new AggregateQueryExecutor<T>(provider, source.Expression, builder);
    }

    /// <summary>
    /// Starts an ad-hoc aggregate query builder from a session query.
    /// Equivalent to <c>session.Query&lt;T&gt;().AggregateQuery(b => ...)</c>.
    /// </summary>
    public static AggregateQueryExecutor<T> AggregateQuery<T>(
        this IQuerySession session,
        Func<AggregateQueryBuilder<T>, AggregateQueryBuilder<T>> build)
        where T : class
    {
        return session.Query<T>().AggregateQuery(build);
    }
}

/// <summary>
/// Executes an ad-hoc aggregate query built with <see cref="AggregateQueryBuilder{T}"/>.
/// Translates the source expression for WHERE/ORDER BY, then overrides SELECT and GROUP BY
/// with the builder's values.
/// </summary>
public class AggregateQueryExecutor<T> where T : class
{
    private readonly SurrealQueryProvider _provider;
    private readonly Expression _expression;
    private readonly AggregateQueryBuilder<T> _builder;

    internal AggregateQueryExecutor(
        SurrealQueryProvider provider,
        Expression expression,
        AggregateQueryBuilder<T> builder)
    {
        _provider = provider;
        _expression = expression;
        _builder = builder;
    }

    /// <summary>
    /// Executes the aggregate query through <see cref="SurrealQueryProvider.ExecuteAggregateAsync{T,TResult}"/>,
    /// which applies ViewName overrides, tenant filters, soft-delete filters, and schema session routing.
    /// </summary>
    public Task<List<TResult>> ToListAsync<TResult>(CancellationToken ct = default)
    {
        return _provider.ExecuteAggregateAsync<T, TResult>(_builder, _expression, ct);
    }

    /// <summary>
    /// Executes the aggregate query and deserializes results into <typeparamref name="T"/>.
    /// </summary>
    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => ToListAsync<T>(ct);
}
