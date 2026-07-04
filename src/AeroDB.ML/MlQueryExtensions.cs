namespace AeroDB;

public static class MlQueryExtensions
{
    public static IMlQuery<TInput, TOutput> Ml<TInput, TOutput>(this IQuerySession session)
        where TInput : class
        where TOutput : class
    {
        var queryable = session.Query<TInput>();
        if (queryable.Provider is SurrealQueryProvider provider)
            return new AeroDBMlQuery<TInput, TOutput>(provider);

        throw new NotSupportedException(
            $"ML queries are only supported on AeroDB query sessions.");
    }
}
