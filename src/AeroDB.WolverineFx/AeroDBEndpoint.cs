using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB.Sable endpoint backed by SurrealDB message storage.
/// Creates <see cref="AeroDBQueueListener"/> and <see cref="AeroDBQueueSender"/>
/// for message processing.
/// </summary>
public sealed class AeroDBEndpoint : Endpoint
{
    /// <summary>
    /// Configure the endpoint with the given URI.
    /// </summary>
    public AeroDBEndpoint(Uri uri) : base(uri, EndpointRole.Application)
    {
        Mode = EndpointMode.Durable;
        BrokerRole = "queue";
    }

    /// <summary>
    /// Create a listener for this endpoint.
    /// </summary>
    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var store = ResolveStore(runtime);
        var options = runtime.Services.GetService<AeroDBTransportOptions>() ?? new AeroDBTransportOptions();
        var logger = runtime.LoggerFactory.CreateLogger<AeroDBQueueListener>();
        var listener = new AeroDBQueueListener(store, receiver, options, logger, Uri);
        await listener.StartAsync();
        return listener;
    }

    /// <summary>
    /// Create a sender for this endpoint.
    /// </summary>
    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var store = ResolveStore(runtime);
        return new AeroDBQueueSender(store, Uri);
    }

    /// <summary>
    /// Supports both Durable and Buffered modes.
    /// </summary>
    protected override bool supportsMode(EndpointMode mode)
    {
        return mode is EndpointMode.Durable or EndpointMode.BufferedInMemory;
    }

    private static AeroDBMessageStore ResolveStore(IWolverineRuntime runtime)
    {
        // Try ancillary store first, then main store, then DI fallback
        var ancillary = runtime.Stores.FindAncillaryStore(typeof(AeroDBMessageStore));
        if (ancillary is AeroDBMessageStore dms)
            return dms;

        if (runtime.Stores.Main is AeroDBMessageStore mainStore)
            return mainStore;

        return (AeroDBMessageStore)runtime.Services.GetRequiredService(typeof(IMessageStore));
    }
}
