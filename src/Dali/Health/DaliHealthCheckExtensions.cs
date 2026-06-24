using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dali;

/// <summary>
/// Extension methods for registering Dali health checks with ASP.NET Core.
/// </summary>
public static class DaliHealthCheckExtensions
{
    /// <summary>
    /// Adds a health check for the Dali async daemon.
    /// Resolves <see cref="AsyncDaemon"/> from the registered <see cref="IDocumentStore"/>.
    /// Must be called after <c>services.AddDali(...)</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check registration name (default: "dali-daemon").</param>
    /// <param name="pollInterval">Expected poll interval for liveness calculation (default: 5 seconds).</param>
    /// <returns>The health checks builder for chaining.</returns>
    public static IHealthChecksBuilder AddDaliCheck(
        this IHealthChecksBuilder builder,
        string name = "dali-daemon",
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);

        builder.Add(new HealthCheckRegistration(
            name,
            sp =>
            {
                var store = sp.GetRequiredService<IDocumentStore>();
                return new DaliDaemonHealthCheck(
                    store.Daemon,
                    interval,
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<DaliDaemonHealthCheck>>());
            },
            null,
            new[] { "dali", "daemon" }));

        return builder;
    }
}
