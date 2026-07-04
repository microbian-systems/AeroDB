using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AeroDB;

/// <summary>
/// DI registration extensions for AeroDB EF Core integration.
/// </summary>
public static class AeroDBEfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers AeroDB alongside EF Core for coordinated transactions.
    /// Consumers must also call <see cref="UseAeroDBTransactionManager{TDbContext}"/>
    /// in their DbContext's <c>OnConfiguring</c> method to wire up the transaction
    /// manager inside EF Core's internal service provider.
    /// </summary>
    public static IServiceCollection AddAeroDBWithEfCore<TDbContext>(
        this IServiceCollection services,
        Action<StoreOptions> configureAeroDB)
        where TDbContext : DbContext
    {
        // Register AeroDB store (singleton)
        var store = Documents.For(configureAeroDB);
        services.AddSingleton(store);

        // Register session factory (scoped per request)
        services.AddScoped(sp =>
        {
            var docStore = sp.GetRequiredService<IDocumentStore>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            // Pass loggerFactory into StoreOptions so sessions get it
            if (loggerFactory is not null)
                docStore.Options.LoggerFactory = loggerFactory;

            return Task.Run(async () =>
                await docStore.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }).ConfigureAwait(false)).GetAwaiter().GetResult();
        });

        return services;
    }

    /// <summary>
    /// Replaces EF Core's default <see cref="IDbContextTransactionManager"/> with
    /// a AeroDB-aware one so that <c>Database.BeginTransaction()</c> coordinates
    /// both SurrealDB and EF Core changes.
    /// Call this inside your DbContext's <c>OnConfiguring</c> method:
    /// <code>
    /// protected override void OnConfiguring(DbContextOptionsBuilder options)
    ///     => options.UseAeroDBTransactionManager&lt;MyDbContext&gt;();
    /// </code>
    /// </summary>
    public static DbContextOptionsBuilder UseAeroDBTransactionManager<TDbContext>(
        this DbContextOptionsBuilder optionsBuilder)
        where TDbContext : DbContext
    {
        optionsBuilder.ReplaceService<IDbContextTransactionManager, AeroDBEfCoreTransactionManager<TDbContext>>();
        return optionsBuilder;
    }

    /// <summary>
    /// Register an EF Core event projection.
    /// </summary>
    public static void AddEfCoreProjection<TDbContext, TProjection>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TProjection : EfCoreEventProjection<TDbContext>, new()
    {
        services.AddScoped<TProjection>(sp =>
        {
            var projection = new TProjection();
            projection.SetDbContextFactory(() => sp.GetRequiredService<TDbContext>());
            return projection;
        });
    }
}
