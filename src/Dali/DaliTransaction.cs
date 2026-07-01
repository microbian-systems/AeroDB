using SurrealDb.Net;

namespace Dali;

/// <summary>
/// Wraps a <see cref="SurrealDbTransaction"/> to provide EF Core-style explicit transaction
/// control with commit/rollback semantics. Created by <see cref="DocumentSession.BeginTransactionAsync"/>
/// and <see cref="DocumentSession.BeginTransaction"/>.
/// </summary>
internal sealed class DaliTransaction : IDaliTransaction
{
    private readonly SurrealDbTransaction _inner;
    private readonly DocumentSession _session;
    private bool _disposed;

    public DaliTransaction(SurrealDbTransaction inner, DocumentSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DaliTransaction));
        await _inner.Commit(ct).ConfigureAwait(false);
        _session.ClearTransaction();
        _disposed = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DaliTransaction));
        await _inner.Cancel(ct).ConfigureAwait(false);
        _session.ClearTransaction();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _session.ClearTransaction();
            _disposed = true;
        }
    }
}
