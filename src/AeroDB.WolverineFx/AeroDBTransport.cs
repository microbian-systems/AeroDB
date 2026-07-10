using AeroDB.Sable;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace AeroDB.WolverineFx;

/// <summary>
/// AeroDB.Sable-backed transport for Wolverine using SurrealDB as the message store.
/// Registers the "AeroDB://" protocol scheme and creates AeroDBEndpoint instances.
/// </summary>
public sealed class AeroDBTransport : ITransport
{
    private readonly Dictionary<Uri, AeroDBEndpoint> _endpoints = new();

    /// <summary>The protocol scheme for this transport.</summary>
    public string Protocol => "AeroDB.Sable";

    /// <summary>Diagnostic name.</summary>
    public string Name => "AeroDB.Sable SurrealDB Transport";

    /// <summary>No reply endpoint for this transport.</summary>
    public Endpoint? ReplyEndpoint() => null;

    /// <summary>
    /// Get or create a AeroDB.Sable endpoint for the given URI.
    /// </summary>
    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        lock (_endpoints)
        {
            if (!_endpoints.TryGetValue(uri, out var endpoint))
            {
                endpoint = new AeroDBEndpoint(uri);
                _endpoints[uri] = endpoint;
            }

            return endpoint;
        }
    }

    /// <summary>
    /// Try to get an existing endpoint for the given URI.
    /// </summary>
    public Endpoint? TryGetEndpoint(Uri uri)
    {
        lock (_endpoints)
        {
            return _endpoints.GetValueOrDefault(uri);
        }
    }

    /// <summary>All registered endpoints.</summary>
    public IEnumerable<Endpoint> Endpoints()
    {
        lock (_endpoints)
        {
            return _endpoints.Values.ToList();
        }
    }

    /// <summary>Initialize the transport. Schema is managed by AeroDBMessageStore.</summary>
    public ValueTask InitializeAsync(IWolverineRuntime runtime) => ValueTask.CompletedTask;

    /// <summary>
    /// Build a health check that probes the SurrealDB connection via <see cref="IDocumentStore"/>.
    /// </summary>
    public WolverineTransportHealthCheck? BuildHealthCheck(IWolverineRuntime runtime)
    {
        var store = runtime.Services.GetRequiredService<IDocumentStore>();
        return new AeroDBHealthCheck(store);
    }

    /// <summary>No stateful resource to build.</summary>
    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = null;
        return false;
    }

    /// <summary>No broker description.</summary>
    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = null!;
        return false;
    }

    /// <summary>Human-readable transport description.</summary>
    string ITransport.Describe() => $"{Name} (scheme '{Protocol}')";
}
