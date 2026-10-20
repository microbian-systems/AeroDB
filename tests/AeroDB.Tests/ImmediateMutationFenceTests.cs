using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Covers managed APIs which execute a mutation immediately instead of queuing
/// it for SaveChangesAsync. They must still respect the history of an explicit
/// version-fenced transaction.
/// </summary>
public sealed class ImmediateMutationFenceTests
{
    [Test]
    public async Task Fence_then_DeleteWhere_is_rejected_and_leaves_documents_unchanged()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "immediate-delete-fence", "fence", 1);
        var target = await SeedVersionedAsync(store, "immediate-delete-target", "delete-target", 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.DeleteWhere<VersionedPerson>(person => person.Name == "delete-target"));
        exception.Message.ShouldContain("version fence");

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fence)).ShouldBe(1);
        (await LoadVersionedAsync(store, target)).ShouldNotBeNull();
    }

    [Test]
    public async Task Fence_then_HardDeleteWhere_is_rejected_and_leaves_documents_unchanged()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "immediate-hard-delete-fence", "fence", 1);
        var target = await SeedVersionedAsync(store, "immediate-hard-delete-target", "hard-delete-target", 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.HardDeleteWhere<VersionedPerson>(person => person.Name == "hard-delete-target"));
        exception.Message.ShouldContain("version fence");

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fence)).ShouldBe(1);
        (await LoadVersionedAsync(store, target)).ShouldNotBeNull();
    }

    [Test]
    public async Task Fence_then_UndoDeleteWhere_is_rejected_and_leaves_the_soft_delete_unchanged()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedSoftVersionedAsync(store, "immediate-undo-fence", "fence", false, 1);
        var target = await SeedSoftVersionedAsync(store, "immediate-undo-target", "undo-target", true, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<SoftDeleteVersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.UndoDeleteWhere<SoftDeleteVersionedPerson>(person => person.Name == "undo-target"));
        exception.Message.ShouldContain("version fence");

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fence)).ShouldBe(1);
        (await ReadDeletedAsync(store, target)).ShouldBeTrue();
    }

    [Test]
    public async Task Fence_then_BulkInsertAsync_is_rejected_and_does_not_insert()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "immediate-bulk-fence", "fence", 1);
        var bulkId = "immediate-bulk-target";

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.BulkInsertAsync(
            [
                new VersionedPerson
                {
                    Id = new RecordIdOf<string>("versioned_person", bulkId),
                    Name = "bulk-target",
                    Version = 1
                }
            ]));
        exception.Message.ShouldContain("version fence");

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fence)).ShouldBe(1);
        (await LoadVersionedAsync(store, RecordId.From("versioned_person", bulkId))).ShouldBeNull();
    }

    [Test]
    public async Task DeleteWhere_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "delete-before-fence", "fence", 1);
        await SeedVersionedAsync(store, "delete-before-target", "delete-before-target", 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.DeleteWhere<VersionedPerson>(person => person.Name == "delete-before-target");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, fence)).ShouldNotBeNull();
    }

    [Test]
    public async Task HardDeleteWhere_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "hard-delete-before-fence", "fence", 1);
        await SeedVersionedAsync(store, "hard-delete-before-target", "hard-delete-before-target", 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.HardDeleteWhere<VersionedPerson>(person => person.Name == "hard-delete-before-target");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, fence)).ShouldNotBeNull();
    }

    [Test]
    public async Task UndoDeleteWhere_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedSoftVersionedAsync(store, "undo-before-fence", "fence", false, 1);
        await SeedSoftVersionedAsync(store, "undo-before-target", "undo-before-target", true, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.UndoDeleteWhere<SoftDeleteVersionedPerson>(person => person.Name == "undo-before-target");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<SoftDeleteVersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await ReadDeletedAsync(store, RecordId.From("soft_delete_versioned_person", "undo-before-target"))).ShouldBeTrue();
    }

    [Test]
    public async Task BulkInsertAsync_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "bulk-before-fence", "fence", 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.BulkInsertAsync(
        [
            new VersionedPerson
            {
                Id = new RecordIdOf<string>("versioned_person", "bulk-before-target"),
                Name = "bulk-before-target",
                Version = 1
            }
        ]);

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, fence)).ShouldNotBeNull();
        (await LoadVersionedAsync(store, RecordId.From("versioned_person", "bulk-before-target"))).ShouldBeNull();
    }

    [Test]
    public async Task Managed_immediate_mutations_remain_usable_without_a_fence()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "control-delete", "control-delete", 1);
        await SeedVersionedAsync(store, "control-hard-delete", "control-hard-delete", 1);
        var soft = await SeedSoftVersionedAsync(store, "control-undo", "control-undo", true, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.DeleteWhere<VersionedPerson>(person => person.Name == "control-delete");
        await session.HardDeleteWhere<VersionedPerson>(person => person.Name == "control-hard-delete");
        await session.UndoDeleteWhere<SoftDeleteVersionedPerson>(person => person.Name == "control-undo");
        await session.BulkInsertAsync(
        [
            new VersionedPerson
            {
                Id = new RecordIdOf<string>("versioned_person", "control-bulk"),
                Name = "control-bulk",
                Version = 1
            }
        ]);
        await transaction.CommitAsync();

        (await LoadVersionedAsync(store, RecordId.From("versioned_person", "control-delete"))).ShouldBeNull();
        (await LoadVersionedAsync(store, RecordId.From("versioned_person", "control-hard-delete"))).ShouldBeNull();
        (await ReadDeletedAsync(store, soft)).ShouldBeFalse();
        (await LoadVersionedAsync(store, RecordId.From("versioned_person", "control-bulk"))).ShouldNotBeNull();
    }

    private static async Task<RecordId> SeedVersionedAsync(
        IDocumentStore store,
        string id,
        string name,
        long version)
    {
        var record = RecordId.From("versioned_person", id);
        await using var session = await store.LightweightSessionAsync();
        session.Insert(new VersionedPerson
        {
            Id = new RecordIdOf<string>("versioned_person", id),
            Name = name,
            Version = version
        });
        await session.SaveChangesAsync();
        return record;
    }

    private static async Task<RecordId> SeedSoftVersionedAsync(
        IDocumentStore store,
        string id,
        string name,
        bool deleted,
        long version)
    {
        var record = RecordId.From("soft_delete_versioned_person", id);
        await using var session = await store.LightweightSessionAsync();
        session.Insert(new SoftDeleteVersionedPerson
        {
            Id = new RecordIdOf<string>("soft_delete_versioned_person", id),
            Name = name,
            Deleted = deleted,
            DeletedAt = deleted ? new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero) : null,
            Version = version
        });
        await session.SaveChangesAsync();
        return record;
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

    private static async Task<bool> ReadDeletedAsync(IDocumentStore store, RecordId record)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            "SELECT VALUE deleted FROM $record;",
            new Dictionary<string, object?> { ["record"] = record });
        response.EnsureAllOks();
        return (response.GetValue<List<bool>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task<VersionedPerson?> LoadVersionedAsync(IDocumentStore store, RecordId record)
    {
        await using var session = await store.LightweightSessionAsync();
        return await session.LoadAsync<VersionedPerson>(record.DeserializeId<string>());
    }
}
