namespace AeroDB;

public static class ViewQueryExtensions
{
    /// <summary>
    /// Queries an existing SurrealDB view or table by name, bypassing the type-based
    /// table name resolution. Use this to query pre-computed views created with
    /// <c>DEFINE TABLE ... AS SELECT ...</c>.
    /// </summary>
    /// <typeparam name="T">The result entity type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="viewName">The SurrealDB view/table name to query.</param>
    public static ISurrealDbQueryable<T> View<T>(this IQuerySession session, string viewName) where T : class
    {
        var queryable = session.Query<T>();
        if (queryable is SurrealDbQueryable<T> sq)
            sq.ViewName = viewName;
        return queryable;
    }
}
