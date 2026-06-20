namespace Dali;

/// <summary>
/// Extension methods on <see cref="IDocumentSession"/> for partial document updates (patching).
/// </summary>
public static class PatchExtensions
{
    /// <summary>
    /// Begins a partial update (patch) for a document identified by its record ID.
    /// The record ID should be just the string identifier (not table-prefixed).
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The document session.</param>
    /// <param name="recordId">The string identifier of the record (e.g., "abc123").</param>
    public static PatchExpression<T> Patch<T>(this IDocumentSession session, string recordId)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return new PatchExpression<T>(session, recordId);
    }
}
