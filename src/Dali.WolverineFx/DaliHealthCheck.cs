using Dali;
using Wolverine.Transports;

namespace Dali.WolverineFx;

/// <summary>
/// Health check for the Dali (SurrealDB) transport.
/// Verifies connectivity by opening a query session and running a heartbeat query.
/// </summary>
internal sealed class DaliHealthCheck : WolverineTransportHealthCheck
{
    private readonly IDocumentStore _store;

    public DaliHealthCheck(IDocumentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override string TransportName => "Dali";

    public override string Protocol => "dali";

    /// <summary>
    /// Check whether the SurrealDB backing store is reachable.
    /// Opens a lightweight query session and runs <c>SELECT 1</c> as a heartbeat.
    /// </summary>
    public override async Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await _store.QuerySessionAsync(cancellationToken);
            await session.RawQueryAsync<object>("SELECT 1", ct: cancellationToken);

            return new TransportHealthResult(
                TransportName,
                Protocol,
                TransportHealthStatus.Healthy,
                "dali:reachable",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new TransportHealthResult(
                TransportName,
                Protocol,
                TransportHealthStatus.Unhealthy,
                $"dali:unreachable: {ex.Message}",
                DateTimeOffset.UtcNow);
        }
    }
}
