using Dali.Diagnostics;
using Dali.Metadata;
using TUnit.Core;

namespace Dali.Tests;

public class DocumentMetadataTests
{
    [Test]
    public async Task DocumentMetadata_sets_last_modified_by()
    {
        var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(new DocumentMetadataListener());
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        if (session is DocumentSession ds)
            ds.CurrentUser = "test-user";

        var doc = new TimestampedDoc { Name = "Test" };
        session.Store(doc);
        await session.SaveChangesAsync();

        doc.LastModifiedBy.ShouldBe("test-user");
    }

    [Test]
    public async Task DocumentMetadata_last_modified_by_is_null_when_no_user()
    {
        var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(new DocumentMetadataListener());
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var doc = new TimestampedDoc { Name = "Test" };
        session.Store(doc);
        await session.SaveChangesAsync();

        doc.LastModifiedBy.ShouldBeNull();
    }

    [Test]
    public async Task DocumentMetadata_sets_created_at_and_last_modified()
    {
        var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(new DocumentMetadataListener());
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var before = DateTimeOffset.UtcNow;
        var doc = new TimestampedDoc { Name = "Test" };
        session.Store(doc);
        await session.SaveChangesAsync();
        var after = DateTimeOffset.UtcNow;

        doc.CreatedAt.ShouldBeGreaterThanOrEqualTo(before);
        doc.CreatedAt.ShouldBeLessThanOrEqualTo(after);
        doc.LastModified.ShouldNotBeNull();
        doc.LastModified!.Value.ShouldBeGreaterThanOrEqualTo(before);
        doc.LastModified!.Value.ShouldBeLessThanOrEqualTo(after);
    }

    [Test]
    public async Task DocumentMetadata_created_at_preserved_on_update()
    {
        var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(new DocumentMetadataListener());
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        if (session is DocumentSession ds)
            ds.CurrentUser = "update-user";

        var doc = new TimestampedDoc { Name = "Original" };
        session.Store(doc);
        await session.SaveChangesAsync();

        var originalCreated = doc.CreatedAt;
        var originalLastModified = doc.LastModified;

        // Wait a bit to ensure timestamp changes
        await Task.Delay(10);

        // Update
        doc.Name = "Updated";
        await session.SaveChangesAsync();

        doc.CreatedAt.ShouldBe(originalCreated);
        doc.LastModified.ShouldNotBeNull();

        if (originalLastModified is not null)
            doc.LastModified!.Value.ShouldBeGreaterThanOrEqualTo(originalLastModified.Value);
    }

    [Test]
    public async Task DocumentMetadata_last_modified_by_updates_on_subsequent_store()
    {
        var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(new DocumentMetadataListener());
        });

        // First store: user1
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            if (session is DocumentSession ds)
                ds.CurrentUser = "user1";

            var doc = new TimestampedDoc { Name = "Test" };
            session.Store(doc);
            await session.SaveChangesAsync();

            doc.LastModifiedBy.ShouldBe("user1");
        }

        // Second store: user2 (different user)
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            if (session is DocumentSession ds)
                ds.CurrentUser = "user2";

            var doc = new TimestampedDoc { Name = "Test-2" };
            session.Store(doc);
            await session.SaveChangesAsync();

            doc.LastModifiedBy.ShouldBe("user2");
        }
    }
}
