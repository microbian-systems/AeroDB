namespace Dali;

/// <summary>
/// Context passed to <see cref="IProjection.ApplyAsync"/> — provides access
/// to the current document session and the events that triggered the projection.
/// </summary>
public interface IProjectionContext
{
    /// <summary>
    /// The document session. Use <c>Session.Store()</c> / <c>Session.Delete()</c>
    /// to persist projected documents. Use <c>Session.Query&lt;T&gt;()</c> to read data.
    /// </summary>
    IDocumentSession Session { get; }

    /// <summary>
    /// Events as bare objects. Deprecated — use <see cref="TypedEvents"/> for typed
    /// event metadata access.
    /// </summary>
    [Obsolete("Use TypedEvents for typed event metadata access.")]
    IReadOnlyList<object> Events { get; }

    /// <summary>
    /// Events with full metadata (version, timestamp, sequence, stream identity).
    /// </summary>
    IReadOnlyList<IEvent> TypedEvents { get; }
}
