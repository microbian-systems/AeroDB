using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Wraps a Dali document session in an EF Core IDbContextTransaction,
/// allowing simultaneous SurrealDB + EF Core operations.
/// </summary>
public class DaliEfCoreTransaction : IDbContextTransaction
{
    private readonly IDocumentSession _daliSession;
    private readonly ILogger<DaliEfCoreTransaction> _logger;
    private bool _disposed;

    public DaliEfCoreTransaction(IDocumentSession daliSession, ILoggerFactory? loggerFactory = null)
    {
        _daliSession = daliSession;
        _logger = loggerFactory?.CreateLogger<DaliEfCoreTransaction>()
            ?? NullLogger<DaliEfCoreTransaction>.Instance;
    }

    public Guid TransactionId { get; } = Guid.NewGuid();

    public IDbContextTransaction? OwnedTransaction { get; set; }

    public void Commit()
    {
        Task.Run(async () => await _daliSession.SaveChangesAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        OwnedTransaction?.Commit();
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Committing Dali EF Core transaction {TransactionId}", TransactionId);
        await _daliSession.SaveChangesAsync(ct).ConfigureAwait(false);
        if (OwnedTransaction is not null)
            await OwnedTransaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public void Rollback()
    {
        _daliSession.ClearChanges();
        OwnedTransaction?.Rollback();
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        _daliSession.ClearChanges();
        if (OwnedTransaction is not null)
            return OwnedTransaction.RollbackAsync(ct);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            OwnedTransaction?.Dispose();
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
