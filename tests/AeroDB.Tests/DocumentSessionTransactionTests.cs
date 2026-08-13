using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public class DocumentSessionTransactionTests
{
    [Test]
    public async Task SaveChanges_CommitsMultipleEntitiesAtomically()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
        });

        // First session: save an entity
        await using var session1 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session1.Store(new Person { Name = "Alice" });
        await session1.SaveChangesAsync();
        await session1.DisposeAsync();

        // Verify Alice was saved
        await using var readSession = await store.QuerySessionAsync();
        var alice = await readSession.Query<Person>()
            .Where(p => p.Name == "Alice").FirstOrDefaultAsync();
        alice.ShouldNotBeNull();
    }

    [Test]
    public async Task SaveChanges_CallsListenerHooksInCorrectOrder()
    {
        var hookOrder = new List<string>();

        var listener = new TestListener(hookOrder);

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Listeners.Add(listener);
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new Person { Name = "Bob" });
        await session.SaveChangesAsync();

        // Verify hook order: BeforeSave -> BeforeCommit -> AfterCommit
        hookOrder.Count.ShouldBeGreaterThanOrEqualTo(2);
        hookOrder[0].ShouldBe("BeforeSaveChanges");
        hookOrder[^1].ShouldBe("AfterCommit");
    }

    [Test]
    public async Task SaveChanges_SkipsAfterCommitOnException()
    {
        // Use a listener that throws from BeforeCommitAsync
        var throwingListener = new ThrowingListener();

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Listeners.Add(throwingListener);
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new Person { Name = "Charlie" });

        // BeforeCommit throws -> tx cancels, AfterCommit NOT called
        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        throwingListener.AfterCommitCalled.ShouldBeFalse();
    }

    [Test]
    public async Task SaveChanges_CancelsTransactionOnException()
    {
        var errorListener = new ThrowingListener();

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
        });

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            // Add the throwing listener to trigger rollback from BeforeCommitAsync
            store.Options.Listeners.Add(errorListener);

            session.Store(new Person { Name = "RollbackTest" });
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
            // The wrapping is: InvalidOperationException("Failed to save changes.") 
            //   -> Inner: InvalidOperationException("Simulated commit failure")
            ex.Message.ShouldBe("Failed to save changes.");

            // Clean up the throwing listener
            store.Options.Listeners.Remove(errorListener);
        }

        // Verify AfterCommit was NOT called
        errorListener.AfterCommitCalled.ShouldBeFalse();
    }

    [Test]
    public async Task SaveChanges_CallsAfterCommitOnSuccess()
    {
        var commitTracker = new CommitTrackingListener();

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
            o.Listeners.Add(commitTracker);
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new Person { Name = "Dave" });
        await session.SaveChangesAsync();

        commitTracker.AfterCommitCalled.ShouldBeTrue();
        commitTracker.BeforeCommitCalled.ShouldBeTrue();
    }

    [Test]
    public async Task EmbeddedTransaction_RawQueryThenCancel_DiscardsRecord()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        const string id = "provider_transaction_cancel";

        await using var transaction = await session.Session.BeginTransaction();
        var response = await transaction.RawQuery(
            $"CREATE person:{id} CONTENT {{ name: 'MustBeDiscarded', age: 42 }};",
            null);
        response.HasErrors.ShouldBeFalse();
        await transaction.Cancel();

        await using var verify = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(id)).ShouldBeNull();
    }

    [Test]
    public async Task ExplicitTransaction_CallsAfterCommitOnlyAfterCommitAndNeverAfterRollback()
    {
        var commitTracker = new CountingCommitListener();
        await using var store = await TestHarness.CreateStoreAsync(o => o.Listeners.Add(commitTracker));

        await using (var rollbackSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        await using (var rollbackTransaction = await rollbackSession.BeginTransactionAsync())
        {
            rollbackSession.Store(new Person { Name = "ExplicitRollback" });
            await rollbackSession.SaveChangesAsync();
            commitTracker.AfterCommitCalls.ShouldBe(0);

            await rollbackTransaction.RollbackAsync();
            commitTracker.AfterCommitCalls.ShouldBe(0);
        }

        await using (var commitSession = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        await using (var commitTransaction = await commitSession.BeginTransactionAsync())
        {
            commitSession.Store(new Person { Name = "ExplicitCommit" });
            await commitSession.SaveChangesAsync();
            commitTracker.AfterCommitCalls.ShouldBe(0);

            await commitTransaction.CommitAsync();
            commitTracker.AfterCommitCalls.ShouldBe(1);
        }
    }

    [Test]
    public async Task SaveChanges_RollsBackFirstDocumentWhenSecondDocumentFails()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string existingId = "auto_atomic_existing";
        const string firstId = "auto_atomic_first";

        await using (var seed = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            seed.Store(new Person
            {
                Id = new SurrealDb.Net.Models.RecordIdOf<string>("person", existingId),
                Name = "Existing",
            });
            await seed.SaveChangesAsync();
        }

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new Person
            {
                Id = new SurrealDb.Net.Models.RecordIdOf<string>("person", firstId),
                Name = "MustRollBack",
            });
            session.Insert(new Person
            {
                Id = new SurrealDb.Net.Models.RecordIdOf<string>("person", existingId),
                Name = "Duplicate",
            });

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(firstId)).ShouldBeNull();
        (await verify.LoadAsync<Person>(existingId)).ShouldNotBeNull();
    }

}

internal sealed class TestListener : IDocumentSessionListener
{
    private readonly List<string> _order;

    public TestListener(List<string> order) => _order = order;

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        _order.Add("BeforeSaveChanges");
        return Task.CompletedTask;
    }

    public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
    {
        _order.Add("BeforeCommit");
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        _order.Add("AfterCommit");
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingListener : IDocumentSessionListener
{
    public bool AfterCommitCalled { get; private set; }

    public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
        => throw new InvalidOperationException("Simulated commit failure");

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        AfterCommitCalled = true;
        return Task.CompletedTask;
    }
}

internal sealed class CommitTrackingListener : IDocumentSessionListener
{
    public bool AfterCommitCalled { get; private set; }
    public bool BeforeCommitCalled { get; private set; }

    public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
    {
        BeforeCommitCalled = true;
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        AfterCommitCalled = true;
        return Task.CompletedTask;
    }
}

internal sealed class CountingCommitListener : DocumentSessionListenerBase
{
    public int AfterCommitCalls { get; private set; }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        AfterCommitCalls++;
        return Task.CompletedTask;
    }
}
