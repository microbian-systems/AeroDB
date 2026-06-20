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
    /// The events that triggered this projection invocation.
    /// </summary>
    IReadOnlyList<object> Events { get; }
}
