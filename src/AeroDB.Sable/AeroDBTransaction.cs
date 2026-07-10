using SurrealDb.Net;

namespace AeroDB.Sable;

/// <summary>
/// Wraps a <see cref="SurrealDbTransaction"/> to provide EF Core-style explicit transaction
/// control with commit/rollback semantics. Created by <see cref="DocumentSession.BeginTransactionAsync"/>
/// and <see cref="DocumentSession.BeginTransaction"/>.
/// </summary>
internal sealed class AeroDBTransaction : IAeroDBTransaction
{
    private readonly DocumentSession _session;
    private bool _disposed;

    public AeroDBTransaction(SurrealDbTransaction inner, DocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AeroDBTransaction));
        await _session.CommitTransactionAsync(ct).ConfigureAwait(false);
        _disposed = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AeroDBTransaction));
        await _session.RollbackTransactionAsync(ct).ConfigureAwait(false);
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _session.RollbackTransactionIfActiveAsync().ConfigureAwait(false);
            _disposed = true;
        }
    }
}
