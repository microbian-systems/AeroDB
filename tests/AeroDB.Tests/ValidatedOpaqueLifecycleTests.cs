using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class ValidatedOpaqueLifecycleTests
{
    [Test]
    public async Task Explicit_transaction_rejects_later_fence_after_before_save_listener_adds_same_table_document()
    {
        var listener = new StoreOnceListener();
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.Store(new Person { Name = "initial-ordinary-write" });

        await session.SaveChangesAsync();
        var names = await ((DocumentSession)session).OperationSession.RawQuery(
            "SELECT VALUE name FROM versioned_person WHERE name = 'listener-added';");
        names.EnsureAllOks();
        (names.GetValue<List<string>>(0) ?? []).ShouldContain("listener-added");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("listener-added-document", 1));
        await transaction.RollbackAsync();
    }
    [Test]
    public async Task Explicit_transaction_rejects_later_fence_after_before_save_listener_queues_opaque_work()
    {
        var listener = new QueueOnceListener();
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            RecordId.From("validated_outbox", "listener-opaque-work"),
            new Dictionary<string, object?> { ["status"] = "queued" }));
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("fence-after-listener-opaque", 1));

        await transaction.RollbackAsync();
    }
    [Test]
    public async Task Same_save_unrelate_is_rejected_when_a_fence_is_active()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var record = RecordId.From("versioned_person", "unrelate-fenced-record");
        await SeedVersionAsync(store, record, 1);
        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(record, 1);
        session.Unrelate(record);

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        (await ReadVersionAsync(store, record)).ShouldBe(1);
    }

    [Test]
    public async Task Explicit_transaction_rejects_later_fence_after_an_earlier_graph_operation()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var graphRecord = RecordId.From("versioned_person", "earlier-graph-operation");
        await SeedVersionAsync(store, graphRecord, 1);
        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        session.Unrelate(graphRecord);
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("fence-after-graph", 1));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, graphRecord)).ShouldBe(1);
    }

    [Test]
    public async Task Explicit_transaction_rejects_later_fence_after_an_earlier_opaque_storage_operation()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        session.QueueOperation(new NoopStorageOperation());
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("fence-after-opaque", 1));

        await transaction.RollbackAsync();
    }

    private sealed class NoopStorageOperation : IStorageOperation
    {
        public Task ExecuteAsync(IDocumentSession session, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class QueueOnceListener : DocumentSessionListenerBase
    {
        private bool _queued;

        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
        {
            if (!_queued)
            {
                session.QueueOperation(new NoopStorageOperation());
                _queued = true;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StoreOnceListener : DocumentSessionListenerBase
    {
        private bool _stored;

        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
        {
            if (!_stored)
            {
                session.Store(new VersionedPerson { Name = "listener-added", Version = 1 });
                _stored = true;
            }

            return Task.CompletedTask;
        }
    }

    private static async Task SeedVersionAsync(IDocumentStore store, RecordId record, long version)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            "CREATE $record CONTENT { name: 'seed', version: $version };",
            new Dictionary<string, object?> { ["record"] = record, ["version"] = version });
        response.EnsureAllOks();
    }

    private static async Task<long> ReadVersionAsync(IDocumentStore store, RecordId record)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            "SELECT VALUE version FROM $record;",
            new Dictionary<string, object?> { ["record"] = record });
        response.EnsureAllOks();
        return (response.GetValue<List<long>>(0) ?? []).ShouldHaveSingleItem();
    }
}
