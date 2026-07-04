using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

/// <summary>
/// Model implementing <see cref="ISoftDeleted"/> for soft-delete tests.
/// </summary>
public class SoftDeletePerson : Record, ISoftDeleted
{
    public string Name { get; set; } = "";
    public DateTimeOffset? DeletedAt { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>
/// Model implementing <see cref="ISoftDeleted"/> and <see cref="IVersioned"/> for combined tests.
/// </summary>
public class SoftDeleteVersionedPerson : Record, ISoftDeleted, IVersioned
{
    public string Name { get; set; } = "";
    public DateTimeOffset? DeletedAt { get; set; }
    public bool Deleted { get; set; }
    public long Version { get; set; }
}

public class SoftDeleteTests
{
    [Test]
    public async Task Soft_delete_sets_deleted_at()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new SoftDeletePerson { Name = "SoftTest1" };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query back to get entity with Id populated (Store doesn't back-populate Id)
        var saved = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "SoftTest1");
        saved.ShouldNotBeNull();

        // Soft delete
        session.Delete(saved!);
        var deletedCount = await session.SaveChangesAsync();
        deletedCount.ShouldBe(1);

        // Verify Deleted and DeletedAt are set on the in-memory entity
        saved!.Deleted.ShouldBeTrue();
        saved.DeletedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Soft_deleted_documents_excluded_from_queries()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var keep = new SoftDeletePerson { Name = "Keep" };
        var discard = new SoftDeletePerson { Name = "Discard" };
        session.Store(keep);
        session.Store(discard);
        await session.SaveChangesAsync();

        // Query back to get entities with Id populated
        var savedKeep = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "Keep");
        var savedDiscard = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "Discard");
        savedKeep.ShouldNotBeNull();
        savedDiscard.ShouldNotBeNull();

        // Soft delete one
        session.Delete(savedDiscard!);
        await session.SaveChangesAsync();

        // Query should only return non-deleted
        var results = await session.Query<SoftDeletePerson>().ToListAsync();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Keep");
    }

    [Test]
    public async Task Non_soft_delete_entity_normal_delete()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new Person { Name = "NormalDel", Age = 50 };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query back to get entity with Id populated
        var saved = await session.Query<Person>().FirstOrDefaultAsync(p => p.Name == "NormalDel");
        saved.ShouldNotBeNull();

        session.Delete(saved!);
        await session.SaveChangesAsync();

        // Person does not implement ISoftDeleted — should be removed
        var results = await session.Query<Person>().ToListAsync();
        results.Any(p => p.Name == "NormalDel").ShouldBeFalse();
    }

    [Test]
    public async Task Soft_delete_is_idempotent()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new SoftDeletePerson { Name = "Idempotent" };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query back to get entity with Id populated
        var saved = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "Idempotent");
        saved.ShouldNotBeNull();

        // Soft delete twice — should not throw
        session.Delete(saved!);
        await session.SaveChangesAsync();

        session.Delete(saved!);
        await session.SaveChangesAsync();

        // Still marked as deleted
        saved!.Deleted.ShouldBeTrue();
        saved.DeletedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Soft_delete_by_id_via_extension()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new SoftDeletePerson { Name = "ByIdTest" };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query to get entity with Id populated
        var saved = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "ByIdTest");
        saved.ShouldNotBeNull();

        // Extract the string ID from the RecordId
        var recordIdStr = saved!.Id is RecordIdOf<string> strRid ? strRid.Id : saved.Id?.ToString();
        recordIdStr.ShouldNotBeNull();

        // Use the SoftDeleteAsync extension to delete by ID
        await session.SoftDeleteAsync<SoftDeletePerson>(recordIdStr!);

        // Query should exclude the soft-deleted record
        var results = await session.Query<SoftDeletePerson>().ToListAsync();
        results.Any(p => p.Name == "ByIdTest").ShouldBeFalse();
    }

    [Test]
    public async Task Soft_delete_then_hard_delete_on_non_soft_entity()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new SoftDeletePerson { Name = "ConvertToHard" };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query back to get entity with Id populated
        var saved = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "ConvertToHard");
        saved.ShouldNotBeNull();

        // Soft delete
        session.Delete(saved!);
        await session.SaveChangesAsync();

        // Soft deleted entities are excluded from queries
        var all = await session.Query<SoftDeletePerson>().ToListAsync();
        all.Any(p => p.Name == "ConvertToHard").ShouldBeFalse();
    }

    [Test]
    public async Task Soft_delete_with_versioned_entity()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new SoftDeleteVersionedPerson { Name = "VersionedSoft" };
        session.Store(person);
        await session.SaveChangesAsync();

        // Query back to get entity with Id populated
        var saved = await session.Query<SoftDeleteVersionedPerson>().FirstOrDefaultAsync(p => p.Name == "VersionedSoft");
        saved.ShouldNotBeNull();

        // Soft delete
        session.Delete(saved!);
        await session.SaveChangesAsync();

        // Verify
        saved!.Deleted.ShouldBeTrue();
        saved.DeletedAt.ShouldNotBeNull();

        // Query should exclude it
        var results = await session.Query<SoftDeleteVersionedPerson>().ToListAsync();
        results.Any(p => p.Name == "VersionedSoft").ShouldBeFalse();
    }

    [Test]
    public async Task Soft_delete_disabled_does_not_filter_queries()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.SoftDeleteEnabled = false;
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var keep = new SoftDeletePerson { Name = "KeepVisible" };
        var discard = new SoftDeletePerson { Name = "DiscardVisible" };
        session.Store(keep);
        session.Store(discard);
        await session.SaveChangesAsync();

        // Query back to get entities with Id populated
        var savedKeep = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "KeepVisible");
        var savedDiscard = await session.Query<SoftDeletePerson>().FirstOrDefaultAsync(p => p.Name == "DiscardVisible");
        savedKeep.ShouldNotBeNull();
        savedDiscard.ShouldNotBeNull();

        // Soft delete one
        session.Delete(savedDiscard!);
        await session.SaveChangesAsync();

        // With SoftDeleteEnabled = false, all records are visible including soft-deleted
        var results = await session.Query<SoftDeletePerson>().ToListAsync();
        results.Count.ShouldBe(2);
    }
}
