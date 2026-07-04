using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB endpoint backed by SurrealDB message storage.
/// Creates <see cref="DaliQueueListener"/> and <see cref="DaliQueueSender"/>
/// for message processing.
/// </summary>
public sealed class DaliEndpoint : Endpoint
{
    /// <summary>
    /// Configure the endpoint with the given URI.
    /// </summary>
    public DaliEndpoint(Uri uri) : base(uri, EndpointRole.Application)
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
        var options = new DaliTransportOptions();
        var logger = runtime.LoggerFactory.CreateLogger<DaliQueueListener>();
        var listener = new DaliQueueListener(store, receiver, options, logger, Uri);
        await listener.StartAsync();
        return listener;
    }

    /// <summary>
    /// Create a sender for this endpoint.
    /// </summary>
    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var store = ResolveStore(runtime);
        return new DaliQueueSender(store, Uri);
    }

    /// <summary>
    /// Supports both Durable and Buffered modes.
    /// </summary>
    protected override bool supportsMode(EndpointMode mode)
    {
        return mode is EndpointMode.Durable or EndpointMode.BufferedInMemory;
    }

    private static DaliMessageStore ResolveStore(IWolverineRuntime runtime)
    {
        // Try ancillary store first, then main store, then DI fallback
        var ancillary = runtime.Stores.FindAncillaryStore(typeof(DaliMessageStore));
        if (ancillary is DaliMessageStore dms)
            return dms;

        if (runtime.Stores.Main is DaliMessageStore mainStore)
            return mainStore;

        return (DaliMessageStore)runtime.Services.GetRequiredService(typeof(IMessageStore));
    }
}
