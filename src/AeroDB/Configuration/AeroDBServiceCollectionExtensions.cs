using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AeroDB;

/// <summary>
/// Extension methods for registering AeroDB services with the DI container.
/// </summary>
public static class AeroDBServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IDocumentStore"/> singleton (fully initialized)
    /// along with <see cref="IAeroDBAdvanced"/> for advanced low-level SDK access.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="StoreOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAeroDB(this IServiceCollection services, Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);

        // Register IDocumentStore as a singleton, initialized immediately
        services.AddSingleton<IDocumentStore>(sp =>
        {
            options.ServiceProvider = sp;
            var store = new DocumentStore(options);
            // Initialize synchronously for configurator discovery.
            // Null sync-context prevents ASP.NET deadlocks.
            var prevCtx = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                store.InitializeAsync().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
            return store;
        });

        // Register advanced access
        if (options.Advanced.SurrealDbClient is not null)
        {
            services.AddSingleton<IAeroDBAdvanced>(sp =>
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
    /// Registers AeroDB using a Marten-compatible service-provider options factory.
    /// </summary>
    public static IServiceCollection AddAeroDB(this IServiceCollection services, Func<IServiceProvider, StoreOptions> configure)
    {
        services.AddSingleton<IDocumentStore>(sp =>
        {
            var options = configure(sp);
            options.ServiceProvider = sp;
            var store = new DocumentStore(options);
            var prevCtx = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                store.InitializeAsync().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
            return store;
        });

        services.AddSingleton<IAeroDBAdvanced>(sp =>
            sp.GetRequiredService<IDocumentStore>().Advanced);

        services.TryAddSingleton<IProjectionCoordinator>(sp =>
            new ProjectionCoordinator(sp.GetRequiredService<IDocumentStore>()));
        services.TryAddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<IProjectionCoordinator>());

        return services;
    }

    /// <summary>
    /// Registers a secondary AeroDB <see cref="IDocumentStore"/> as a keyed scoped service,
    /// keyed by <typeparamref name="T"/>. Use <c>AddAeroDBStore&lt;IInvoicingStore&gt;(opts => ...)</c>
    /// for multi-database scenarios where <typeparamref name="T"/> is a marker interface
    /// extending <see cref="IDocumentStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="StoreOptions"/>.</param>
    /// <typeparam name="T">A marker interface (e.g., <c>IInvoicingStore : IDocumentStore</c>).</typeparam>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAeroDBStore<T>(this IServiceCollection services, Action<StoreOptions> configure)
        where T : class, IDocumentStore
    {
        var options = new StoreOptions();
        configure(options);

        services.AddKeyedScoped<IDocumentStore>(typeof(T).FullName!, (sp, _) =>
        {
            options.ServiceProvider = sp;
            var store = new DocumentStore(options);
            // Initialize synchronously for configurator discovery.
            // Null sync-context prevents ASP.NET deadlocks.
            var prevCtx = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                store.InitializeAsync().GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
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
    public static IServiceCollection ConfigureAeroDB<T>(this IServiceCollection services, Action<StoreOptions> configure)
        where T : class
    {
        // Store the config for later use — pattern: register a named options configurator
        return services.Configure<T>(o =>
        {
            // Placeholder — typed config per service
        });
    }
}
