using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroDB;

/// <summary>
/// Wraps a AeroDB document session in an EF Core IDbContextTransaction,
/// allowing simultaneous SurrealDB + EF Core operations.
/// </summary>
public class AeroDBEfCoreTransaction : IDbContextTransaction
{
    private readonly IDocumentSession _AeroDBSession;
    private readonly ILogger<AeroDBEfCoreTransaction> _logger;
    private bool _disposed;

    public AeroDBEfCoreTransaction(IDocumentSession AeroDBSession, ILoggerFactory? loggerFactory = null)
    {
        _AeroDBSession = AeroDBSession;
        _logger = loggerFactory?.CreateLogger<AeroDBEfCoreTransaction>()
            ?? NullLogger<AeroDBEfCoreTransaction>.Instance;
    }

    public Guid TransactionId { get; } = Guid.NewGuid();

    public IDbContextTransaction? OwnedTransaction { get; set; }

    public void Commit()
    {
        Task.Run(async () => await _AeroDBSession.SaveChangesAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        OwnedTransaction?.Commit();
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Committing AeroDB EF Core transaction {TransactionId}", TransactionId);
        await _AeroDBSession.SaveChangesAsync(ct).ConfigureAwait(false);
        if (OwnedTransaction is not null)
            await OwnedTransaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public void Rollback()
    {
        _AeroDBSession.ClearChanges();
        OwnedTransaction?.Rollback();
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        _AeroDBSession.ClearChanges();
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
