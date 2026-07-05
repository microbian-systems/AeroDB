namespace AeroDB;

/// <summary>
/// Receives notifications when documents of type T are created, updated, or deleted.
/// Register on <see cref="SessionOptions.Subscribers"/> for per-session subscriptions.
/// </summary>
public interface ISubscriber<T> where T : class
{
    /// <summary>Called after a document of type T is created.</summary>
    Task CreatedAsync(T document, CancellationToken ct);

    /// <summary>Called after a document of type T is updated.</summary>
    Task UpdatedAsync(T document, CancellationToken ct);

    /// <summary>Called after a document of type T is deleted.</summary>
    Task DeletedAsync(T document, CancellationToken ct);
}
