using Microsoft.Extensions.DependencyInjection;

namespace Dali;

/// <summary>
/// Extension methods for registering Dali services with the DI container.
/// </summary>
public static class DaliServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IDocumentStore"/> singleton (fully initialized)
    /// along with <see cref="IDaliAdvanced"/> for advanced low-level SDK access.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="StoreOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDali(this IServiceCollection services, Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);

        // Register IDocumentStore as a singleton, initialized immediately
        services.AddSingleton<IDocumentStore>(sp =>
        {
            options.ServiceProvider = sp;
            var store = new DocumentStore(options);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });

        // Register advanced access
        if (options.Advanced.SurrealDbClient is not null)
        {
            services.AddSingleton<IDaliAdvanced>(sp =>
                sp.GetRequiredService<IDocumentStore>().Advanced);
        }

        return services;
    }
}
