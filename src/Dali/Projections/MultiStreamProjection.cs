using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Creates/updates projected documents that span multiple event streams.
/// The document identity is defined by the subclass (e.g., from a correlation ID
/// shared across streams).
/// </summary>
/// <typeparam name="T">The projected document type (must extend <see cref="Record"/>).</typeparam>
public abstract class MultiStreamProjection<T> : InlineProjection<T> where T : Record
{
    /// <summary>
    /// Determines the projected document identity from the events.
    /// This can be a string (record key) or any identifier.
    /// </summary>
    protected abstract override object GetDocumentId(IReadOnlyList<object> events);
}
