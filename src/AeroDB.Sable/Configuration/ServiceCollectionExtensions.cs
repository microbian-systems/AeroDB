using Microsoft.Extensions.DependencyInjection;

namespace AeroDB.Sable;

/// <summary>
/// Extension methods for registering AeroDB.Sable configuration with the DI container.
/// </summary>
public static class AeroDBConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IConfigureAeroDB"/> implementation as a singleton in the
    /// service collection. Instances are expected to be added to <see cref="StoreOptions.Configurators"/>
    /// by the consumer before store initialization.
    /// </summary>
    public static IServiceCollection ConfigureAeroDB<TConfigurator>(this IServiceCollection services)
        where TConfigurator : class, IConfigureAeroDB
    {
        services.AddSingleton<IConfigureAeroDB, TConfigurator>();
        return services;
    }

    /// <summary>
    /// Registers an <see cref="IAsyncConfigureAeroDB"/> implementation as a singleton.
    /// Async configurators are applied during <c>DocumentStore.InitializeAsync</c>
    /// after sync <see cref="IConfigureAeroDB"/> configurators.
    /// </summary>
    public static IServiceCollection ConfigureAeroDBAsync<TConfigurator>(this IServiceCollection services)
        where TConfigurator : class, IAsyncConfigureAeroDB
    {
        services.AddSingleton<IAsyncConfigureAeroDB, TConfigurator>();
        return services;
    }
}
