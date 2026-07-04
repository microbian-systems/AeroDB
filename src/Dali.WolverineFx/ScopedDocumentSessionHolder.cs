using AeroDB;

namespace Dali.WolverineFx;

/// <summary>
/// Scope-local carrier for the outbox-enrolled <see cref="IDocumentSession"/> a handler is using.
/// When a handler falls back to service location, Wolverine's generated code primes this holder
/// so a service-located <see cref="IDocumentSession"/> / <see cref="IQuerySession"/> resolves
/// to the SAME session enrolled with the active outbox. See GH-3001.
/// </summary>
public sealed class ScopedDocumentSessionHolder
{
    /// <summary>
    /// The outbox-enrolled session to use for service-location resolution.
    /// Null when not in a handler scope.
    /// </summary>
    public IDocumentSession? Session { get; set; }
}
