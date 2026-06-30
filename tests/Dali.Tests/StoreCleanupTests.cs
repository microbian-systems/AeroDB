using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

public class StoreCleanupTests
{
    public class CleanupSimpleDoc : Record
    {
        public string Name { get; set; } = "";
    }

    public class SoftDeleteDoc : Record, ISoftDeleted
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? DeletedAt { get; set; }
        public bool Deleted { get; set; }
    }

    [Test]
    public async Task BulkInsertAsync_InsertsAllDocuments()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var docs = Enumerable.Range(1, 50)
            .Select(i => new CleanupSimpleDoc { Name = $"Doc{i}" })
            .ToList();

        var count = await session.BulkInsertAsync(docs, batchSize: 10);
        count.ShouldBe(50);

        var all = await session.Query<CleanupSimpleDoc>().ToListAsync();
        all.Count.ShouldBe(50);
    }

    [Test]
    public async Task BulkInsertAsync_EmptyList_DoesNotThrow()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var empty = new List<CleanupSimpleDoc>();
        var count = await session.BulkInsertAsync(empty);
        count.ShouldBe(0);
    }

    [Test]
    public async Task BulkInsertAsync_BatchSizeOne_Works()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var docs = Enumerable.Range(1, 3)
            .Select(i => new CleanupSimpleDoc { Name = $"Batch{i}" })
            .ToList();

        var count = await session.BulkInsertAsync(docs, batchSize: 1);
        count.ShouldBe(3);

        var all = await session.Query<CleanupSimpleDoc>().ToListAsync();
        all.Count.ShouldBe(3);
    }

    [Test]
    public async Task CleanDeletedDocumentsAsync_RemovesSoftDeleted()
    {
        // Create store without registering SoftDeleteDoc in schema (avoids AutoCreate conflicts).
        // Register the mapping after initialization so CleanDeletedDocumentsAsync can find it.
        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Schema.For<SoftDeleteDoc>();
        await using var session = await store.LightweightSessionAsync();

        var doc = new SoftDeleteDoc { Name = "ToClean" };
        session.Store(doc);
        await session.SaveChangesAsync();

        // Query back to get the persisted entity with Id populated
        var saved = await session.Query<SoftDeleteDoc>().FirstOrDefaultAsync(d => d.Name == "ToClean");
        saved.ShouldNotBeNull();

        // Soft delete the document
        session.Delete(saved!);
        await session.SaveChangesAsync();

        saved!.Deleted.ShouldBeTrue();
        saved.DeletedAt.ShouldNotBeNull();

        // Clean soft-deleted documents (older than TimeSpan.Zero = all soft-deleted).
        // Returns the count of tables cleaned (1 for SoftDeleteDoc mapping).
        var cleaned = await store.CleanDeletedDocumentsAsync(TimeSpan.Zero);
        cleaned.ShouldBe(1);

        // Verify the document is permanently removed
        var remaining = await session.Query<SoftDeleteDoc>().ToListAsync();
        remaining.Any(d => d.Name == "ToClean").ShouldBeFalse();
    }
}
