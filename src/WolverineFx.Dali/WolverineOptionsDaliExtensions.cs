using Dali;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SurrealDb.Net;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace WolverineFx.Dali;

/// <summary>
/// Extension methods for integrating Wolverine with Dali (SurrealDB) persistence.
/// </summary>
public static class WolverineOptionsDaliExtensions
{
    /// <summary>
    /// Configure Wolverine to use Dali for message persistence, outbox, sagas, and transport.
    /// Registers DaliMessageStore, DaliOutboxedSessionFactory, and all supporting services.
    /// </summary>
    public static void IntegrateWithDali(this WolverineOptions options, IServiceProvider serviceProvider)
    {
        var client = serviceProvider.GetRequiredService<ISurrealDbClient>();
        var logger = serviceProvider.GetRequiredService<ILogger<DaliMessageStore>>();
        var store = serviceProvider.GetRequiredService<IDocumentStore>();

        var messageStore = new DaliMessageStore(client, logger);

        options.Services.AddSingleton<IMessageStore>(messageStore);
        options.Services.AddSingleton(messageStore);

        options.Services.AddSingleton(sp =>
        {
            var docStore = sp.GetRequiredService<IDocumentStore>();
            var daliStore = sp.GetRequiredService<DaliMessageStore>();
            var factoryLogger = sp.GetRequiredService<ILogger<DaliOutboxedSessionFactory>>();
            return new DaliOutboxedSessionFactory(docStore, daliStore, factoryLogger);
        });

        options.Services.AddScoped<ScopedDocumentSessionHolder>();

        PreferScopedSession<IDocumentSession>(options.Services);
        PreferScopedSession<IQuerySession>(options.Services);

        options.Services.AddSingleton<IWolverineExtension>(new DaliIntegration());

        WolverineOptionsSubscriptionExtensions.EnsureSubscriptionHostedService(options.Services);
    }

    /// <summary>
    /// Configure Wolverine to persist messages using Dali (SurrealDB).
    /// </summary>
    public static void PersistMessagesWithDali(
        this WolverineOptions options,
        ISurrealDbClient client,
        ILogger<DaliMessageStore> logger)
    {
        var messageStore = new DaliMessageStore(client, logger);

        options.Services.AddSingleton<IMessageStore>(messageStore);
        options.Services.AddSingleton(messageStore);
        options.Services.AddSingleton<IWolverineExtension>(new DaliIntegration());
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
