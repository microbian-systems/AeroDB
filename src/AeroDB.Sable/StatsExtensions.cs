namespace AeroDB.Sable;

public static class StatsExtensions
{
    public static ISableQueryable<T> Stats<T>(this IQueryable<T> queryable, out QueryStatistics stats)
        where T : class
    {
        if (queryable is not SableQueryable<T> sq)
            throw new InvalidOperationException("Stats is only supported on SableQueryable<T>.");

        sq.QueryStats = new QueryStatistics();
        stats = sq.QueryStats;
        return sq;
    }
}
