namespace Dali;

/// <summary>
/// Marker interface for DI-registered (or directly added) Dali configuration modules.
/// Implementations are resolved from <see cref="StoreOptions.Configurators"/> and applied
/// during <see cref="DocumentStore.InitializeAsync"/>.
/// </summary>
public interface IConfigureDali
{
    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> to apply document-level
    /// schema configuration (indices, tenancy policy, etc.).
    /// </summary>
    void Configure(StoreOptions options);
}
