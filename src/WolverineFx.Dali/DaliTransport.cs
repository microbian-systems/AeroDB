using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace WolverineFx.Dali;

/// <summary>
/// Dali-backed transport for Wolverine using SurrealDB as the message store.
/// Registers the "dali://" protocol scheme and creates DaliEndpoint instances.
/// </summary>
public sealed class DaliTransport : ITransport
{
    private readonly Dictionary<Uri, DaliEndpoint> _endpoints = new();

    /// <summary>The protocol scheme for this transport.</summary>
    public string Protocol => "dali";

    /// <summary>Diagnostic name.</summary>
    public string Name => "Dali SurrealDB Transport";

    /// <summary>No reply endpoint for this transport.</summary>
    public Endpoint? ReplyEndpoint() => null;

    /// <summary>
    /// Get or create a Dali endpoint for the given URI.
    /// </summary>
    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        lock (_endpoints)
        {
            if (!_endpoints.TryGetValue(uri, out var endpoint))
            {
                endpoint = new DaliEndpoint(uri);
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

    /// <summary>Initialize the transport. Schema is managed by DaliMessageStore.</summary>
    public ValueTask InitializeAsync(IWolverineRuntime runtime) => ValueTask.CompletedTask;

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
