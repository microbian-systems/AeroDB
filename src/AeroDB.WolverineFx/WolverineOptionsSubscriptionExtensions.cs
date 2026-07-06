using AeroDB;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

#pragma warning disable AERODB001

/// <summary>
/// Extension methods for registering AeroDB event subscriptions with Wolverine.
/// </summary>
public static class WolverineOptionsSubscriptionExtensions
{
    /// <summary>
    /// Register a AeroDB event subscription as a singleton.
    /// The subscription will be polled by the AeroDB subscription daemon automatically.
    /// </summary>
    public static WolverineOptions AddAeroDBSubscription<T>(this WolverineOptions options)
        where T : class, IAeroDBSubscription
    {
        options.Services.AddSingleton<IAeroDBSubscription, T>();
        options.Services.TryAddSingleton(
            sp => new AeroDBSubscriptionRunner(
                sp.GetRequiredService<T>(),
                sp.GetRequiredService<IWolverineRuntime>()));
        return options;
    }

    /// <summary>
    /// Register a AeroDB event subscription with a specific instance.
    /// </summary>
    public static WolverineOptions AddAeroDBSubscription(this WolverineOptions options, IAeroDBSubscription subscription)
    {
        options.Services.AddSingleton(subscription);
        options.Services.TryAddSingleton(
            sp => new AeroDBSubscriptionRunner(
                subscription,
                sp.GetRequiredService<IWolverineRuntime>()));
        return options;
    }

    /// <summary>
    /// Ensure the AeroDB subscription hosted service is registered.
    /// Called automatically by <see cref="WolverineOptionsAeroDBExtensions.IntegrateWithAeroDB"/>
    /// but can also be called manually for advanced scenarios.
    /// </summary>
    internal static void EnsureSubscriptionHostedService(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService>(sp =>
            {
                var runners = sp.GetServices<AeroDBSubscriptionRunner>();
                var logger = sp.GetService<ILogger<AeroDBSubscriptionHostedService>>();
                return new AeroDBSubscriptionHostedService(sp, runners, logger);
            }));
    }
}
