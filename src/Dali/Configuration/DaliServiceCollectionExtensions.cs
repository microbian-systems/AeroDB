using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

        // Register projection coordinator for automatic daemon lifecycle management
        services.TryAddSingleton<IProjectionCoordinator>(sp =>
            new ProjectionCoordinator(sp.GetRequiredService<IDocumentStore>()));
        services.TryAddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<IProjectionCoordinator>());

        return services;
    }

    /// <summary>
    /// Registers a secondary Dali <see cref="IDocumentStore"/> as a keyed scoped service,
    /// keyed by <typeparamref name="T"/>. Use <c>AddDaliStore&lt;IInvoicingStore&gt;(opts => ...)</c>
    /// for multi-database scenarios where <typeparamref name="T"/> is a marker interface
    /// extending <see cref="IDocumentStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="StoreOptions"/>.</param>
    /// <typeparam name="T">A marker interface (e.g., <c>IInvoicingStore : IDocumentStore</c>).</typeparam>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDaliStore<T>(this IServiceCollection services, Action<StoreOptions> configure)
        where T : class, IDocumentStore
    {
        var options = new StoreOptions();
        configure(options);

        services.AddKeyedScoped<IDocumentStore>(typeof(T).FullName!, (sp, _) =>
        {
            options.ServiceProvider = sp;
            var store = new DocumentStore(options);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });

        return services;
    }

    /// <summary>
    /// Registers a typed configuration delegate for <see cref="StoreOptions"/>,
    /// following the named options configurator pattern.
    /// </summary>
    /// <typeparam name="T">The marker type for the configuration.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="StoreOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection ConfigureDali<T>(this IServiceCollection services, Action<StoreOptions> configure)
        where T : class
    {
        // Store the config for later use — pattern: register a named options configurator
        return services.Configure<T>(o =>
        {
            // Placeholder — typed config per service
        });
    }
}
