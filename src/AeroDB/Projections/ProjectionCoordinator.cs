using Microsoft.Extensions.Hosting;

namespace AeroDB;

/// <summary>
/// Wraps the <see cref="AsyncDaemon"/> in an <see cref="IHostedService"/>
/// for automatic lifecycle management. Registered in DI by <c>AddDali()</c>.
/// </summary>
public class ProjectionCoordinator : IProjectionCoordinator
{
    private readonly IDocumentStore _store;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Creates a <see cref="ProjectionCoordinator"/> wrapping the given store's daemon.
    /// </summary>
    /// <param name="store">The document store whose <see cref="IDocumentStore.Daemon"/> is managed.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="store"/> is null.</exception>
    public ProjectionCoordinator(IDocumentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public AsyncDaemon? Daemon => _store.Daemon;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Daemon is not null && !Daemon.Health.IsRunning)
        {
            Daemon.Start(DefaultPollInterval);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Daemon is not null && Daemon.Health.IsRunning)
        {
            await Daemon.StopAsync().ConfigureAwait(false);
        }
    }
}
