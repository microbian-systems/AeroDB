using Microsoft.Extensions.DependencyInjection;

namespace AeroDB;

/// <summary>
/// Extension methods for registering AeroDB configuration with the DI container.
/// </summary>
public static class DaliConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IConfigureDali"/> implementation as a singleton in the
    /// service collection. Instances are expected to be added to <see cref="StoreOptions.Configurators"/>
    /// by the consumer before store initialization.
    /// </summary>
    public static IServiceCollection ConfigureDali<TConfigurator>(this IServiceCollection services)
        where TConfigurator : class, IConfigureDali
    {
        services.AddSingleton<IConfigureDali, TConfigurator>();
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IAsyncConfigureDali"/> implementation as a singleton.
    /// Async configurators are applied during <c>DocumentStore.InitializeAsync</c>
    /// after sync <see cref="IConfigureDali"/> configurators.
    /// </summary>
    public static IServiceCollection ConfigureDaliAsync<TConfigurator>(this IServiceCollection services)
        where TConfigurator : class, IAsyncConfigureDali
    {
        services.AddSingleton<IAsyncConfigureDali, TConfigurator>();
        return services;
    }
}
