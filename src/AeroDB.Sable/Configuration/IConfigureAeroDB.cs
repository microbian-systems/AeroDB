namespace AeroDB.Sable;

/// <summary>
/// DI-registered (or directly added) AeroDB.Sable configuration module.
/// Implementations are applied during <see cref="DocumentStore.InitializeAsync"/>.
///
/// <para>
/// Implementers may override either overload:
/// <list type="bullet">
///   <item><see cref="Configure(StoreOptions)"/> — simple configuration (backward-compatible).</item>
///   <item><see cref="Configure(IServiceProvider?,StoreOptions)"/> — configuration with DI service access.</item>
/// </list>
/// The two-parameter overload has a default implementation that delegates to the single-parameter
/// overload, so existing implementations need no changes.
/// </para>
/// </summary>
public interface IConfigureAeroDB
{
    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> to apply document-level
    /// schema configuration (indices, tenancy policy, etc.).
    /// </summary>
    void Configure(StoreOptions options);

    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> with optional
    /// <see cref="IServiceProvider"/> for DI-resolved services.
    /// Default implementation delegates to <see cref="Configure(StoreOptions)"/>.
    /// </summary>
    void Configure(IServiceProvider? services, StoreOptions options)
        => Configure(options);

    /// <summary>
    /// Called during <c>DocumentStore.InitializeAsync</c> with a fluent <see cref="IAeroSchemaBuilder"/>
    /// that provides a unified entry point for document, event stream, and projection configuration.
    /// Default implementation is a no-op — override to use the higher-level fluent API.
    /// <para>
    /// Prefer this overload over <see cref="Configure(StoreOptions)"/> for new configuration.
    /// Both overloads are called during initialization.
    /// </para>
    /// </summary>
    void Configure(IAeroSchemaBuilder schema)
    {
        // Default: no-op for backward compatibility.
    }
}
