using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dali;

/// <summary>
/// DI registration extensions for Dali EF Core integration.
/// </summary>
public static class DaliEfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Dali alongside EF Core for coordinated transactions.
    /// Consumers must also call <see cref="UseDaliTransactionManager{TDbContext}"/>
    /// in their DbContext's <c>OnConfiguring</c> method to wire up the transaction
    /// manager inside EF Core's internal service provider.
    /// </summary>
    public static IServiceCollection AddDaliWithEfCore<TDbContext>(
        this IServiceCollection services,
        Action<StoreOptions> configureDali)
        where TDbContext : DbContext
    {
        // Register Dali store (singleton)
        var store = Documents.For(configureDali);
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
                await docStore.LightweightSessionAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        });

        return services;
    }

    /// <summary>
    /// Replaces EF Core's default <see cref="IDbContextTransactionManager"/> with
    /// a Dali-aware one so that <c>Database.BeginTransaction()</c> coordinates
    /// both SurrealDB and EF Core changes.
    /// Call this inside your DbContext's <c>OnConfiguring</c> method:
    /// <code>
    /// protected override void OnConfiguring(DbContextOptionsBuilder options)
    ///     => options.UseDaliTransactionManager&lt;MyDbContext&gt;();
    /// </code>
    /// </summary>
    public static DbContextOptionsBuilder UseDaliTransactionManager<TDbContext>(
        this DbContextOptionsBuilder optionsBuilder)
        where TDbContext : DbContext
    {
        optionsBuilder.ReplaceService<IDbContextTransactionManager, DaliEfCoreTransactionManager<TDbContext>>();
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
