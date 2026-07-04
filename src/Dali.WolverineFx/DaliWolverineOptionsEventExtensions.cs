using AeroDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Dali.WolverineFx;

/// <summary>
/// Extension methods for configuring AeroDB event forwarding through Wolverine.
/// Event forwarding is automatically wired when sessions are opened via
/// <see cref="DaliOutboxedSessionFactory"/>. This method provides additional
/// store-level configuration for event-related options.
/// </summary>
public static class DaliWolverineOptionsEventExtensions
{
    /// <summary>
    /// Configure Wolverine to forward events appended to AeroDB event streams
    /// as Wolverine messages. Events are published atomically within the
    /// same transaction as the document session's save operation.
    ///
    /// Event forwarding is automatically enabled when sessions are created
    /// through <see cref="DaliOutboxedSessionFactory"/>. This method provides
    /// additional configuration hook for the AeroDB <see cref="StoreOptions"/>.
    /// </summary>
    /// <param name="options">The Wolverine options.</param>
    /// <param name="configureStore">
    /// Optional callback to configure the underlying AeroDB <see cref="StoreOptions"/>.
    /// Use this to set up event-related options on the document store.
    /// </param>
    public static void ForwardDaliEventsToWolverine(
        this Wolverine.WolverineOptions options,
        Action<StoreOptions>? configureStore = null)
    {
        // Event forwarding is wired automatically by DaliOutboxedSessionFactory.
        // This extension method is a configuration hook for users who need to
        // customize store-level event options.
        //
        // If a configureStore callback is provided, register it as an IConfigureDali
        // so DocumentStore applies it during InitializeAsync.
        if (configureStore is not null)
        {
            options.Services.AddSingleton<IConfigureDali>(
                new DelegateDaliConfigurator(configureStore));
        }
    }

    private sealed class DelegateDaliConfigurator : IConfigureDali
    {
        private readonly Action<StoreOptions> _configure;
        public DelegateDaliConfigurator(Action<StoreOptions> configure) => _configure = configure;
        public void Configure(StoreOptions options) => _configure(options);
    }
}
