using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// EF Core transaction manager that wraps Dali sessions.
/// Use when both EF Core and Dali operations must be atomic.
/// </summary>
public class DaliEfCoreTransactionManager<TDbContext> : IDbContextTransactionManager
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly IDocumentSession _daliSession;
    private readonly ILogger<DaliEfCoreTransactionManager<TDbContext>> _logger;

    public DaliEfCoreTransactionManager(TDbContext dbContext, IDocumentSession daliSession, ILoggerFactory? loggerFactory = null)
    {
        _dbContext = dbContext;
        _daliSession = daliSession;
        _logger = loggerFactory?.CreateLogger<DaliEfCoreTransactionManager<TDbContext>>()
            ?? NullLogger<DaliEfCoreTransactionManager<TDbContext>>.Instance;
    }

    public IDbContextTransaction? CurrentTransaction { get; private set; }

    public IDbContextTransaction BeginTransaction()
        => Task.Run(async () => await BeginTransactionAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Beginning Dali EF Core transaction");
        var efTransaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var daliTransaction = new DaliEfCoreTransaction(_daliSession)
        {
            OwnedTransaction = efTransaction
        };
        CurrentTransaction = daliTransaction;
        return daliTransaction;
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
