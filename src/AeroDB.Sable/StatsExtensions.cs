namespace AeroDB.Sable;

public static class StatsExtensions
{
    public static ISurrealDbQueryable<T> Stats<T>(this IQueryable<T> queryable, out QueryStatistics stats)
        where T : class
    {
        if (queryable is not SurrealDbQueryable<T> sq)
            throw new InvalidOperationException("Stats is only supported on SurrealDbQueryable<T>.");

        sq.QueryStats = new QueryStatistics();
        stats = sq.QueryStats;
        return sq;
    }
}
