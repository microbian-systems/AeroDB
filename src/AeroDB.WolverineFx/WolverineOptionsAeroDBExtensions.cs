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
    /// Configures Wolverine's multi-tenant durable message persistence using
    /// tenant routing services constructed and owned by the application
    /// container. The directory and document-store factory must be concrete,
    /// container-constructible implementations registered by this call.
    /// </summary>
    /// <typeparam name="TDirectory">The server-owned tenant-store directory implementation.</typeparam>
    /// <typeparam name="TDocumentStoreFactory">The server-owned tenant document-store factory implementation.</typeparam>
    public static void PersistMessagesWithAeroDBTenancy<TDirectory, TDocumentStoreFactory>(this WolverineOptions options)
        where TDirectory : class, IAeroDBTenantStoreDirectory
        where TDocumentStoreFactory : class, IAeroDBTenantDocumentStoreFactory
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureAeroDBCompositeRegistrationIsExclusive(options.Services);
        EnsureTenantRoutingServicesAreUnregistered(
            options.Services,
            typeof(TDirectory),
            typeof(TDocumentStoreFactory));

        options.Services.AddSingleton<IAeroDBTenantStoreDirectory, TDirectory>();
        options.Services.AddSingleton<IAeroDBTenantDocumentStoreFactory, TDocumentStoreFactory>();
        ConfigureAeroDBCompositeRegistration(options.Services);
    }

    /// <summary>
    /// Configures only Wolverine's multi-tenant durable message persistence.
    /// This deliberately does not register AeroDB's native transport, saga
    /// support, code-generation frames, or persistence handlers. Use normal
    /// Wolverine transports for delivery and retain document-store ownership of
    /// the borrowed main client.
    /// </summary>
    public static void PersistMessagesWithAeroDBTenancy(
        this WolverineOptions options,
        IAeroDBTenantStoreDirectory directory,
        IAeroDBTenantDocumentStoreFactory documentStores)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(documentStores);
        EnsureAeroDBCompositeRegistrationIsExclusive(options.Services);
        EnsureTenantRoutingServicesAreUnregistered(options.Services);

        options.Services.AddSingleton<IAeroDBTenantStoreDirectory>(directory);
        options.Services.AddSingleton<IAeroDBTenantDocumentStoreFactory>(documentStores);
        ConfigureAeroDBCompositeRegistration(options.Services);
    }

    private static void ConfigureAeroDBCompositeRegistration(IServiceCollection services)
    {
        services.AddSingleton<AeroDBRuntimeNodeIdentity>();

        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IDocumentStore>();
            var rawMain = new AeroDBMessageStore(
                store.Client,
                sp.GetRequiredService<ILogger<AeroDBMessageStore>>(),
                sp.GetService<ILoggerFactory>(),
                store.Options,
                ownsClient: false,
                runtimeIdentity: sp.GetRequiredService<AeroDBRuntimeNodeIdentity>());
            return new AeroDBCompositeMainMessageStore(
                rawMain,
                new AeroDBMainStoreIdentity(
                    store.Options.Namespace ?? throw new InvalidOperationException("The main document store requires a namespace."),
                    store.Options.Database ?? throw new InvalidOperationException("The main document store requires a database.")));
        });
        services.AddSingleton<AeroDBTenantStoreRegistry>();
        services.AddSingleton<IAeroDBTenantStorePairFactory, AeroDBTenantStorePairFactory>();
        services.AddSingleton<AeroDBTenantStorePairCache>();
        services.AddSingleton(sp => new AeroDBTenantedMessageSource(
            sp.GetRequiredService<IAeroDBTenantStoreDirectory>(),
            sp.GetRequiredService<AeroDBTenantStoreRegistry>(),
            sp.GetRequiredService<AeroDBTenantStorePairCache>(),
            sp.GetRequiredService<AeroDBCompositeMainMessageStore>().Identity,
            new AeroDBTenantMessageStoreReadiness(
                () => sp.GetRequiredService<IWolverineRuntime>())));
        services.AddSingleton<IAeroDBTenantedOutboxedSessionFactory, AeroDBTenantedOutboxedSessionFactory>();
        services.AddSingleton<IMessageStore>(sp => new MultiTenantedMessageStore(
            sp.GetRequiredService<AeroDBCompositeMainMessageStore>(),
            sp.GetRequiredService<IWolverineRuntime>(),
            sp.GetRequiredService<AeroDBTenantedMessageSource>()));
        services.AddSingleton<CompositeAeroDBMessageStoreRegistration>();
    }

    /// <summary>
    /// Configure Wolverine to use AeroDB.Sable for message persistence, outbox, sagas, and transport.
    /// Registers AeroDBMessageStore, AeroDBOutboxedSessionFactory, and all supporting services.
    /// </summary>
    public static void IntegrateWithAeroDB(this WolverineOptions options, IServiceProvider serviceProvider)
    {
        EnsureAeroDBLegacyRegistrationIsAllowed(options.Services);
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
        EnsureAeroDBLegacyRegistrationIsAllowed(options.Services);
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

    private static void EnsureAeroDBCompositeRegistrationIsExclusive(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CompositeAeroDBMessageStoreRegistration)
            || descriptor.ServiceType == typeof(AeroDBMessageStore)
            || (descriptor.ServiceType == typeof(IMessageStore)
                && descriptor.ImplementationInstance is AeroDBMessageStore)))
        {
            throw new InvalidOperationException(
                "AeroDB composite durability cannot be combined with another AeroDB message-store registration.");
        }
    }

    private static void EnsureTenantRoutingServicesAreUnregistered(
        IServiceCollection services,
        Type? directoryImplementationType = null,
        Type? documentStoreFactoryImplementationType = null)
    {
        var prohibitedServiceTypes = new HashSet<Type>
        {
            typeof(IAeroDBTenantStoreDirectory),
            typeof(IAeroDBTenantDocumentStoreFactory)
        };

        if (directoryImplementationType is not null)
            prohibitedServiceTypes.Add(directoryImplementationType);
        if (documentStoreFactoryImplementationType is not null)
            prohibitedServiceTypes.Add(documentStoreFactoryImplementationType);

        var routingService = services.FirstOrDefault(descriptor =>
            prohibitedServiceTypes.Contains(descriptor.ServiceType));

        if (routingService is not null)
        {
            throw new InvalidOperationException(
                $"AeroDB composite durability tenant routing is already registered for '{routingService.ServiceType.FullName}'.");
        }
    }

    private static void EnsureAeroDBLegacyRegistrationIsAllowed(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CompositeAeroDBMessageStoreRegistration)))
        {
            throw new InvalidOperationException(
                "AeroDB legacy message persistence cannot be combined with the tenant composite registration.");
        }
    }

    private sealed class CompositeAeroDBMessageStoreRegistration;
}
