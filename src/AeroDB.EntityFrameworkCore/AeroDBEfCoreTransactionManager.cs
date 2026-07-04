using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroDB;

/// <summary>
/// EF Core transaction manager that wraps AeroDB sessions.
/// Use when both EF Core and AeroDB operations must be atomic.
/// </summary>
public class AeroDBEfCoreTransactionManager<TDbContext> : IDbContextTransactionManager
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly IDocumentSession _AeroDBSession;
    private readonly ILogger<AeroDBEfCoreTransactionManager<TDbContext>> _logger;

    public AeroDBEfCoreTransactionManager(TDbContext dbContext, IDocumentSession AeroDBSession, ILoggerFactory? loggerFactory = null)
    {
        _dbContext = dbContext;
        _AeroDBSession = AeroDBSession;
        _logger = loggerFactory?.CreateLogger<AeroDBEfCoreTransactionManager<TDbContext>>()
            ?? NullLogger<AeroDBEfCoreTransactionManager<TDbContext>>.Instance;
    }

    public IDbContextTransaction? CurrentTransaction { get; private set; }

    public IDbContextTransaction BeginTransaction()
        => Task.Run(async () => await BeginTransactionAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Beginning AeroDB EF Core transaction");
        var efTransaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var AeroDBTransaction = new AeroDBEfCoreTransaction(_AeroDBSession)
        {
            OwnedTransaction = efTransaction
        };
        CurrentTransaction = AeroDBTransaction;
        return AeroDBTransaction;
    }

    public void CommitTransaction()
    {
        if (CurrentTransaction is not null)
        {
            CurrentTransaction.Commit();
            CurrentTransaction = null;
        }
    }

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        if (CurrentTransaction is not null)
        {
            await CurrentTransaction.CommitAsync(ct).ConfigureAwait(false);
            CurrentTransaction = null;
        }
    }

    public void RollbackTransaction()
    {
        if (CurrentTransaction is not null)
        {
            CurrentTransaction.Rollback();
            CurrentTransaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (CurrentTransaction is not null)
        {
            await CurrentTransaction.RollbackAsync(ct).ConfigureAwait(false);
            CurrentTransaction = null;
        }
    }

    public void ResetState()
    {
        CurrentTransaction = null;
    }

    public Task ResetStateAsync(CancellationToken ct = default)
    {
        CurrentTransaction = null;
        return Task.CompletedTask;
    }
}
