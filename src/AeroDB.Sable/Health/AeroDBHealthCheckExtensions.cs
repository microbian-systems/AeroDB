using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AeroDB.Sable;

/// <summary>
/// Extension methods for registering AeroDB.Sable health checks with ASP.NET Core.
/// </summary>
public static class AeroDBHealthCheckExtensions
{
    /// <summary>
    /// Adds a health check for the AeroDB.Sable async daemon.
    /// Resolves <see cref="AsyncDaemon"/> from the registered <see cref="IDocumentStore"/>.
    /// Must be called after <c>services.AddAeroDB(...)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check registration name (default: "AeroDB.Sable-daemon").</param>
    /// <param name="pollInterval">Expected poll interval for liveness calculation (default: 5 seconds).</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddAeroDBCheck(
        this IHealthChecksBuilder builder,
        string name = "AeroDB.Sable-daemon",
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);

        builder.Add(new HealthCheckRegistration(
            name,
            sp =>
            {
                var store = sp.GetRequiredService<IDocumentStore>();
                return new AeroDBDaemonHealthCheck(
                    store.Daemon,
                    interval,
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<AeroDBDaemonHealthCheck>>());
            },
            null,
            new[] { "AeroDB.Sable", "daemon" }));

        return builder;
    }
}
