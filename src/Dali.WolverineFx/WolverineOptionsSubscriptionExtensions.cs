using AeroDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace Dali.WolverineFx;

/// <summary>
/// Extension methods for registering AeroDB event subscriptions with Wolverine.
/// </summary>
public static class WolverineOptionsSubscriptionExtensions
{
    /// <summary>
    /// Register a AeroDB event subscription as a singleton.
    /// The subscription will be polled by the AeroDB subscription daemon automatically.
    /// </summary>
    public static WolverineOptions AddDaliSubscription<T>(this WolverineOptions options)
        where T : class, IDaliSubscription
    {
        options.Services.AddSingleton<IDaliSubscription, T>();
        options.Services.TryAddSingleton(
            sp => new DaliSubscriptionRunner(
                sp.GetRequiredService<T>(),
                sp.GetRequiredService<IWolverineRuntime>()));
        return options;
    }

    /// <summary>
    /// Register a AeroDB event subscription with a specific instance.
    /// </summary>
    public static WolverineOptions AddDaliSubscription(this WolverineOptions options, IDaliSubscription subscription)
    {
        options.Services.AddSingleton(subscription);
        options.Services.TryAddSingleton(
            sp => new DaliSubscriptionRunner(
                subscription,
                sp.GetRequiredService<IWolverineRuntime>()));
        return options;
    }

    /// <summary>
    /// Ensure the AeroDB subscription hosted service is registered.
    /// Called automatically by <see cref="WolverineOptionsDaliExtensions.IntegrateWithDali"/>
    /// but can also be called manually for advanced scenarios.
    /// </summary>
    internal static void EnsureSubscriptionHostedService(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService>(sp =>
            {
                var runners = sp.GetServices<DaliSubscriptionRunner>();
                var logger = sp.GetService<ILogger<DaliSubscriptionHostedService>>();
                return new DaliSubscriptionHostedService(sp, runners, logger);
            }));
    }
}
