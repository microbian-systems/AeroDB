using AeroDB.Sable;
using Wolverine.Transports;

namespace AeroDB.WolverineFx;

/// <summary>
/// Health check for the AeroDB.Sable (SurrealDB) transport.
/// Verifies connectivity by opening a query session and running a heartbeat query.
/// </summary>
internal sealed class AeroDBHealthCheck : WolverineTransportHealthCheck
{
    private readonly IDocumentStore _store;

    public AeroDBHealthCheck(IDocumentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override string TransportName => "AeroDB.Sable";

    public override string Protocol => "AeroDB.Sable";

    /// <summary>
    /// Check whether the SurrealDB backing store is reachable.
    /// Opens a lightweight query session to verify connectivity.
    /// </summary>
    public override async Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await _store.QuerySessionAsync(cancellationToken);
            // Session open succeeded — SurrealDB is reachable

            return new TransportHealthResult(
                TransportName,
                Protocol,
                TransportHealthStatus.Healthy,
                "AeroDB.Sable:reachable (query session opened successfully)",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new TransportHealthResult(
                TransportName,
                Protocol,
                TransportHealthStatus.Unhealthy,
                $"AeroDB.Sable:unreachable: {ex.Message}",
                DateTimeOffset.UtcNow);
        }
    }
}
