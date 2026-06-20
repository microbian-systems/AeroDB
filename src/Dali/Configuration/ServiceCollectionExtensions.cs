using Microsoft.Extensions.DependencyInjection;

namespace Dali;

/// <summary>
/// Extension methods for registering Dali configuration with the DI container.
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
}
