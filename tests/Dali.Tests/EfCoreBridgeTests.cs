using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using TUnit.Core;

namespace Dali.Tests;

public class EfCoreBridgeTests
{
    [Test]
    public async Task DaliEfCoreTransaction_commit_calls_saveChangesAsync()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var tx = new DaliEfCoreTransaction(mockSession);

        await tx.CommitAsync();

        await mockSession.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DaliEfCoreTransaction_commit_calls_owned_transaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockOwned = Substitute.For<IDbContextTransaction>();
        var tx = new DaliEfCoreTransaction(mockSession)
        {
            OwnedTransaction = mockOwned
        };

        await tx.CommitAsync();

        await mockOwned.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DaliEfCoreTransaction_commit_calls_both()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockOwned = Substitute.For<IDbContextTransaction>();
        var tx = new DaliEfCoreTransaction(mockSession)
        {
            OwnedTransaction = mockOwned
        };

        await tx.CommitAsync();

        await mockSession.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await mockOwned.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DaliEfCoreTransaction_rollback_calls_owned_transaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockOwned = Substitute.For<IDbContextTransaction>();
        var tx = new DaliEfCoreTransaction(mockSession)
        {
            OwnedTransaction = mockOwned
        };

        await tx.RollbackAsync();

        await mockOwned.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void DaliEfCoreTransaction_sync_commit_calls_saveChanges()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var tx = new DaliEfCoreTransaction(mockSession);

        tx.Commit();

        mockSession.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void DaliEfCoreTransaction_sync_rollback_calls_owned()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockOwned = Substitute.For<IDbContextTransaction>();
        var tx = new DaliEfCoreTransaction(mockSession)
        {
            OwnedTransaction = mockOwned
        };

        tx.Rollback();

        mockOwned.Received(1).Rollback();
    }

    [Test]
    public void DaliEfCoreTransaction_has_transactionId()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var tx = new DaliEfCoreTransaction(mockSession);

        tx.TransactionId.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task DaliEfCoreTransaction_dispose_disposes_owned()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockOwned = Substitute.For<IDbContextTransaction>();
        var tx = new DaliEfCoreTransaction(mockSession)
        {
            OwnedTransaction = mockOwned
        };

        await tx.DisposeAsync();

        mockOwned.Received(1).Dispose();
    }

    [Test]
    public async Task DaliEfCoreTransaction_multiple_commits_safe()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var tx = new DaliEfCoreTransaction(mockSession);

        await tx.CommitAsync();
        // Second commit should be safe
        await tx.CommitAsync();

        await mockSession.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── TransactionManager tests (behavior-only, no real BeginTransactionAsync) ─

    [Test]
    public async Task DaliEfCoreTransactionManager_commit_when_no_transaction_does_nothing()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Should not throw
        await manager.CommitTransactionAsync();
    }

    [Test]
    public async Task DaliEfCoreTransactionManager_rollback_when_no_transaction_does_nothing()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Should not throw
        await manager.RollbackTransactionAsync();
    }

    [Test]
    public void DaliEfCoreTransactionManager_resetState_clears_transaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Manually set a transaction
        var tx = new DaliEfCoreTransaction(mockSession);
        // Use reflection to set private field since we can't access it
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);
        manager.CurrentTransaction.ShouldNotBeNull();

        manager.ResetState();

        manager.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public async Task DaliEfCoreTransactionManager_resetStateAsync_clears_transaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Manually set a transaction
        var tx = new DaliEfCoreTransaction(mockSession);
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);

        await manager.ResetStateAsync();

        manager.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public async Task DaliEfCoreTransactionManager_commitTransactionAsync_uses_currentTransaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Inject a DaliEfCoreTransaction wrapping the mock session
        var tx = new DaliEfCoreTransaction(mockSession);
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);

        await manager.CommitTransactionAsync();

        await mockSession.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        manager.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public async Task DaliEfCoreTransactionManager_rollbackTransactionAsync_clears_transaction()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        // Inject a DaliEfCoreTransaction
        var tx = new DaliEfCoreTransaction(mockSession);
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);

        await manager.RollbackTransactionAsync();

        manager.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public void DaliEfCoreTransactionManager_commitTransaction_sync_commits()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        var tx = new DaliEfCoreTransaction(mockSession);
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);

        manager.CommitTransaction();

        mockSession.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        manager.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public void DaliEfCoreTransactionManager_rollbackTransaction_sync_rolls_back()
    {
        var mockSession = Substitute.For<IDocumentSession>();
        var mockDbContext = Substitute.For<DbContext>();
        var manager = new DaliEfCoreTransactionManager<DbContext>(mockDbContext, mockSession);

        var tx = new DaliEfCoreTransaction(mockSession);
        var field = typeof(DaliEfCoreTransactionManager<DbContext>)
            .GetField("<CurrentTransaction>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(manager, tx);

        manager.RollbackTransaction();

        manager.CurrentTransaction.ShouldBeNull();
    }
}
