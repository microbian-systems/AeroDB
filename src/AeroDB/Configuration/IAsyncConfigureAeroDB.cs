namespace AeroDB;

/// <summary>
/// Async DI-registered AeroDB configuration module.
/// Applied during <see cref="DocumentStore.InitializeAsync"/> after sync
/// <see cref="IConfigureAeroDB"/> configurators, but before schema initialization.
/// </summary>
public interface IAsyncConfigureAeroDB
{
    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> to apply async
    /// schema configuration (projections, event types, etc.).
    /// </summary>
    Task ConfigureAsync(StoreOptions options, CancellationToken ct = default);
}
