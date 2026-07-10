using AeroDB.Sable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Extension methods for integrating Wolverine with AeroDB.Sable (SurrealDB) persistence.
/// </summary>
public static class WolverineOptionsAeroDBExtensions
{
    /// <summary>
    /// Configure Wolverine to use AeroDB.Sable for message persistence, outbox, sagas, and transport.
    /// Registers AeroDBMessageStore, AeroDBOutboxedSessionFactory, and all supporting services.
    /// </summary>
    public static void IntegrateWithAeroDB(this WolverineOptions options, IServiceProvider serviceProvider)
    {
        var client = serviceProvider.GetRequiredService<ISurrealDbClient>();
        var logger = serviceProvider.GetRequiredService<ILogger<AeroDBMessageStore>>();
        var store = serviceProvider.GetRequiredService<IDocumentStore>();
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

        var messageStore = new AeroDBMessageStore(client, logger, loggerFactory, store.Options);

        options.Services.AddSingleton<IMessageStore>(messageStore);
        options.Services.AddSingleton(messageStore);

        options.Services.AddSingleton(sp =>
        {
            var docStore = sp.GetRequiredService<IDocumentStore>();
            var AeroDBStore = sp.GetRequiredService<AeroDBMessageStore>();
            var factoryLogger = sp.GetRequiredService<ILogger<AeroDBOutboxedSessionFactory>>();
            return new AeroDBOutboxedSessionFactory(docStore, AeroDBStore, factoryLogger);
        });

        options.Services.AddScoped<ScopedDocumentSessionHolder>();

        PreferScopedSession<IDocumentSession>(options.Services);
        PreferScopedSession<IQuerySession>(options.Services);

        options.Services.AddSingleton<IWolverineExtension>(new AeroDBIntegration());

        WolverineOptionsSubscriptionExtensions.EnsureSubscriptionHostedService(options.Services);
    }

    /// <summary>
    /// Configure Wolverine to persist messages using AeroDB.Sable (SurrealDB).
    /// </summary>
    public static void PersistMessagesWithAeroDB(
        this WolverineOptions options,
        ISurrealDbClient client,
        ILogger<AeroDBMessageStore> logger)
    {
        var messageStore = new AeroDBMessageStore(client, logger);

        options.Services.AddSingleton<IMessageStore>(messageStore);
        options.Services.AddSingleton(messageStore);
        options.Services.AddSingleton<IWolverineExtension>(new AeroDBIntegration());
    }

    private static void PreferScopedSession<T>(IServiceCollection services) where T : class
    {
        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(T));
        if (descriptor == null) return;

        Func<IServiceProvider, object> original;
        if (descriptor.ImplementationFactory != null)
            original = descriptor.ImplementationFactory;
        else if (descriptor.ImplementationInstance != null)
            original = _ => descriptor.ImplementationInstance;
        else if (descriptor.ImplementationType != null)
            original = sp => ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        else
            return;

        services.Remove(descriptor);
        services.AddScoped<T>(sp =>
        {
            if (sp.GetRequiredService<ScopedDocumentSessionHolder>().Session is T primed)
                return primed;
            return (T)original(sp);
        });
    }
}
