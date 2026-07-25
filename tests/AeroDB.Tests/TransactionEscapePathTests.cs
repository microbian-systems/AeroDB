using AeroDB.Sable;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

public class TransactionEscapePathTests
{
    [Test]
    public async Task DeferredPatch_IsRolledBackWhenBeforeCommitFails()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string id = "transactional_patch_rollback";

        await using (var seed = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            seed.Store(new Person
            {
                Id = new RecordIdOf<string>("person", id),
                Name = "Patch target",
                Age = 20,
            });
            await seed.SaveChangesAsync();
        }

        var listener = new EscapePathThrowingListener();
        store.Options.Listeners.Add(listener);
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Patch<Person>(id).Set(person => person.Age, 99);
            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }
        store.Options.Listeners.Remove(listener);

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var person = await verify.LoadAsync<Person>(id);
        person.ShouldNotBeNull();
        person.Age.ShouldBe(20);
    }

    [Test]
    public async Task QueuedSql_IsRolledBackWhenBeforeCommitFails()
    {
        var listener = new EscapePathThrowingListener();
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.QueueSqlCommand(
                "{database}",
                "CREATE queue_test_doc SET Value = $p0",
                "must-roll-back");
            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }
        store.Options.Listeners.Remove(listener);

        await using var verify = await store.QuerySessionAsync();
        var records = await verify.RawQueryAsync<QueueTestDoc>(
            "SELECT * FROM queue_test_doc WHERE Value = 'must-roll-back'");
        records.ShouldBeEmpty();
    }

    [Test]
    public async Task InlineProjectionEvent_IsRolledBackWhenBeforeCommitFails()
    {
        var listener = new EscapePathThrowingListener();
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        store.Options.Projections.Add(new SideEffectTestProjection());

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            await session.Events.Append("escape-path-trigger", [
                new SideEffectTriggered { TriggerId = "escape-path" },
            ]);

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }
        store.Options.Listeners.Remove(listener);

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.Events.FetchStream("escape-path-trigger")).ShouldBeEmpty();
        (await verify.Events.FetchStream("gen-stream")).ShouldBeEmpty();
        (await verify.LoadAsync<SideEffectDoc>("saga-se-1")).ShouldBeNull();
    }

    [Test]
    public async Task AutoEventAppend_IsCommittedBySaveChanges()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string streamId = "auto-event-commit";

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            var appended = await session.Events.Append(streamId, [
                new SideEffectTriggered { TriggerId = "commit" },
            ]);
            appended.Count.ShouldBe(1);

            await session.SaveChangesAsync();
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var events = await verify.Events.FetchStream(streamId);
        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<SideEffectTriggered>().TriggerId.ShouldBe("commit");
    }

    [Test]
    public async Task FailedMultiEventAppend_CannotCommitPartialTransaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string streamId = "failed-multi-event-append";

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            await Should.ThrowAsync<Exception>(() => session.Events.Append(streamId, [
                new SideEffectTriggered { TriggerId = "must-roll-back" },
                new ThrowingSerializationEvent(),
            ]));

            (await session.SaveChangesAsync()).ShouldBe(0);
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.Events.FetchStream(streamId)).ShouldBeEmpty();
    }

    [Test]
    public async Task RejectedCrossDatabaseSave_CannotReplayDiscardedEventsOnRetry()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<SalesOrder>().Schema("sales");
            options.Schema.AutoCreateDatabases = true;
            options.Projections.Add(new SideEffectTestProjection());
        });
        const string streamId = "rejected-cross-database-event";

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            await session.Events.Append(streamId, [
                new SideEffectTriggered { TriggerId = "discarded-cross-database" },
            ]);
            var salesOrder = new SalesOrder { OrderNumber = "discarded", Amount = 10m };
            session.Store(salesOrder);

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

            session.Eject(salesOrder);
            (await session.SaveChangesAsync()).ShouldBe(0);
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.Events.FetchStream(streamId)).ShouldBeEmpty();
        (await verify.Events.FetchStream("gen-stream")).ShouldBeEmpty();
        (await verify.LoadAsync<SideEffectDoc>("saga-se-1")).ShouldBeNull();
    }

    [Test]
    public async Task PatchResponseError_RollsBackEarlierDocumentWrite()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string targetId = "required-patch-target";
        const string markerId = "patch-error-marker";

        await using (var seed = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            seed.Store(new RequiredPerson
            {
                Id = new RecordIdOf<string>("required_person", targetId),
                Name = "Required",
                Age = 30,
            });
            await seed.SaveChangesAsync();
        }

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new Person
            {
                Id = new RecordIdOf<string>("person", markerId),
                Name = "Must roll back",
            });
            session.Patch<RequiredPerson>(targetId)
                .Append(person => person.Name, 'x');

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(markerId)).ShouldBeNull();
        var target = await verify.LoadAsync<RequiredPerson>(targetId);
        target.ShouldNotBeNull();
        target.Name.ShouldBe("Required");
    }

    [Test]
    public async Task EventAppend_UsesExplicitTransactionAndIsDiscardedOnRollback()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        const string streamId = "explicit-event-rollback";

        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await session.Events.Append(streamId, [new SideEffectTriggered { TriggerId = "rollback" }]);
            await session.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.Events.FetchStream(streamId)).ShouldBeEmpty();
    }
}

internal sealed class ThrowingSerializationEvent
{
    public string Value => throw new InvalidOperationException("Force event serialization failure.");
}

internal sealed class EscapePathThrowingListener : DocumentSessionListenerBase
{
    public override Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
        => throw new InvalidOperationException("Force transaction rollback.");
}
