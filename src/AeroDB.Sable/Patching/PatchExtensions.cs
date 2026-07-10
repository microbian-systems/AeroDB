namespace AeroDB.Sable;

using System.Linq.Expressions;

/// <summary>
/// Extension methods on <see cref="IDocumentSession"/> for partial document updates (patching).
/// Equivalent to Marten's <c>Marten.Patching.PatchingExtensions</c>.
/// </summary>
public static class PatchExtensions
{
    /// <summary>
    /// Begins a partial update (patch) for a document identified by its record ID.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The document session.</param>
    /// <param name="recordId">The string identifier of the record.</param>
    public static IPatchExpression<T> Patch<T>(this IDocumentSession session, string recordId)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return new PatchExpression<T>(session, recordId);
    }

    /// <summary>
    /// Begins a partial update (patch) for a document identified by its integer ID.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    public static IPatchExpression<T> Patch<T>(this IDocumentSession session, int id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        return new PatchExpression<T>(session, id.ToString());
    }

    /// <summary>
    /// Begins a partial update (patch) for a document identified by its long ID.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    public static IPatchExpression<T> Patch<T>(this IDocumentSession session, long id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        return new PatchExpression<T>(session, id.ToString());
    }

    /// <summary>
    /// Begins a partial update (patch) for a document identified by its Guid ID.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    public static IPatchExpression<T> Patch<T>(this IDocumentSession session, Guid id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        return new PatchExpression<T>(session, id.ToString());
    }

    /// <summary>
    /// Begins a partial update (patch) for documents matching a filter expression.
    /// The patch will be applied to ALL documents satisfying the predicate.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The document session.</param>
    /// <param name="filter">Expression filter for documents to patch.</param>
    public static IPatchExpression<T> Patch<T>(this IDocumentSession session, Expression<Func<T, bool>> filter)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(filter);

        // Build a filter-based PatchExpression that will use the expression
        // to construct the WHERE clause during ApplyAsync
        return new FilteredPatchExpression<T>(session, filter);
    }
}
