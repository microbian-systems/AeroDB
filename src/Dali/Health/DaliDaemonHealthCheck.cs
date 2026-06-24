using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// ASP.NET Core health check for the Dali async daemon.
/// Reports Healthy/Degraded/Unhealthy based on daemon state.
/// </summary>
public class DaliDaemonHealthCheck : IHealthCheck
{
    private readonly AsyncDaemon? _daemon;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<DaliDaemonHealthCheck> _logger;

    public DaliDaemonHealthCheck(AsyncDaemon? daemon, TimeSpan pollInterval, ILogger<DaliDaemonHealthCheck>? logger = null)
    {
        _daemon = daemon;
        _pollInterval = pollInterval;
        _logger = logger ?? NullLogger<DaliDaemonHealthCheck>.Instance;
    }

    /// <summary>
    /// Evaluates daemon health based on the latest <see cref="DaemonHealthState"/> snapshot.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="HealthCheckResult"/> with status and diagnostic data.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (_daemon is null)
            return Task.FromResult(HealthCheckResult.Healthy("Dali daemon not configured."));

        var health = _daemon.Health;

        if (!health.IsRunning)
            return Task.FromResult(HealthCheckResult.Unhealthy("Dali daemon is stopped."));

        // Healthy: last success within 2x poll interval
        if (health.LastSuccess.HasValue &&
            (DateTimeOffset.UtcNow - health.LastSuccess.Value) < _pollInterval * 2)
        {
            var data = new Dictionary<string, object>
            {
                ["HighWaterSequence"] = health.HighWaterSequence,
                ["LagCount"] = health.LagCount
            };
            return Task.FromResult(HealthCheckResult.Healthy(
                $"Daemon running. High water: {health.HighWaterSequence}, lag: {health.LagCount}",
                data));
        }

        // Degraded: running but no recent success
        if (health.IsRunning)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Daemon running but no recent success. Last error: {health.LastException ?? "none"}"));

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
