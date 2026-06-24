using Microsoft.EntityFrameworkCore;

namespace Dali;

/// <summary>
/// Projection that writes to EF Core alongside Dali in the same transaction.
/// The <typeparamref name="TDbContext"/> provides the relational data access.
/// </summary>
public abstract class EfCoreEventProjection<TDbContext> : IProjection
    where TDbContext : DbContext
{
    /// <summary>
    /// Gets the EF Core DbContext factory. Set by the projection runner.
    /// </summary>
    protected Func<TDbContext>? DbContextFactory { get; private set; }

    public abstract Type[] EventTypes { get; }
    public virtual ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;

    /// <summary>
    /// Set the DbContext factory. Called by the projection runner or DI registration.
    /// </summary>
    public void SetDbContextFactory(Func<TDbContext> factory)
    {
        DbContextFactory = factory;
    }

    /// <summary>
    /// Apply the projection using an EF Core DbContext.
    /// Override to use <see cref="DbContext"/> methods.
    /// </summary>
    protected abstract Task ApplyAsync(TDbContext dbContext, IReadOnlyList<IEvent> events, CancellationToken ct);

    async Task IProjection.ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        if (DbContextFactory == null)
            throw new InvalidOperationException("EfCoreEventProjection must be initialized with a DbContext factory.");

        var dbContext = DbContextFactory();
        await using (dbContext.ConfigureAwait(false))
        {
            await ApplyAsync(dbContext, context.TypedEvents.ToList().AsReadOnly(), ct).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public virtual Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        // Rebuild not supported for EF Core projections — data lives in EF Core
        return Task.CompletedTask;
    }
}
