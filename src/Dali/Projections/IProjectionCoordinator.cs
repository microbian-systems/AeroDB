using Microsoft.Extensions.Hosting;

namespace Dali;

/// <summary>
/// Manages the lifecycle of the async projection daemon.
/// Implements <see cref="IHostedService"/> for automatic start/stop with the application.
/// </summary>
public interface IProjectionCoordinator : IHostedService
{
    /// <summary>
    /// Gets the underlying daemon instance, if one is active.
    /// </summary>
    AsyncDaemon? Daemon { get; }
}
