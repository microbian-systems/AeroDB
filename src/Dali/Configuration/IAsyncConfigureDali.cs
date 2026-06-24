namespace Dali;

/// <summary>
/// Async DI-registered Dali configuration module.
/// Applied during <see cref="DocumentStore.InitializeAsync"/> after sync
/// <see cref="IConfigureDali"/> configurators, but before schema initialization.
/// </summary>
public interface IAsyncConfigureDali
{
    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> to apply async
    /// schema configuration (projections, event types, etc.).
    /// </summary>
    Task ConfigureAsync(StoreOptions options, CancellationToken ct = default);
}
