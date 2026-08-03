using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public class WriteModel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }

    public void Apply(OrderEvent e)
    {
        Count++;
    }
}



public class ConcurrencyTests
{
    /// <summary>
    /// Helper to cast <see cref="IDocumentSession"/> to concrete <see cref="DocumentSession"/>
    /// so internal methods like <c>AddOperation</c> are accessible from tests.
    /// </summary>
    private static DocumentSession AsDoc(IDocumentSession s) => (DocumentSession)s;

    [Test]
    public async Task Per_document_concurrency_mapping_tracks_versions_and_rejects_stale_updates()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            var mapping = options.Schema.For<VersionedPerson>();
            mapping.UseOptimisticConcurrency = true;
            mapping.SetSchemaMode(SchemaMode.Flexible);
        });

        var person = new VersionedPerson { Name = "Mapped concurrency", Age = 20 };
        await using (var seed = await store.OpenSessionAsync(new SessionOptions()))
        {
            seed.Store(person);
            await seed.SaveChangesAsync();
        }
        person.Version.ShouldBe(1);

        await using var first = await store.OpenSessionAsync(new SessionOptions());
        await using var second = await store.OpenSessionAsync(new SessionOptions());
        var firstCopy = await first.LoadAsync<VersionedPerson>(person.Id!);
        var secondCopy = await second.LoadAsync<VersionedPerson>(person.Id!);
        firstCopy.ShouldNotBeNull();
        secondCopy.ShouldNotBeNull();

        firstCopy.Name = "First writer";
        first.Store(firstCopy);
        await first.SaveChangesAsync();
        firstCopy.Version.ShouldBe(2);

        secondCopy.Name = "Stale writer";
        second.Store(secondCopy);
        var exception = await Should.ThrowAsync<ConcurrencyException>(
            () => second.SaveChangesAsync());

        exception.ExpectedVersion.ShouldBe(1);
        exception.ActualVersion.ShouldBe(2);
    }

    /// <summary>
    /// A new <see cref="VersionedPerson"/> starts at Version=0.
    /// On first save it should be incremented to 1 without error,
    /// even though the version field may not be persisted by the
    /// in-memory engine's CBOR serializer.
    /// </summary>
    [Test]
    public async Task Concurrency_first_save_no_conflict()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseOptimisticConcurrency = true;
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new VersionedPerson { Name = "FirstSave", Age = 20 };
        person.Version.ShouldBe(0);

        session.Store(person);

        var savedCount = await session.SaveChangesAsync();
        savedCount.ShouldBe(1);

        // After save, in-memory entity should have version 1 (incremented before persist)
        person.Version.ShouldBe(1);
    }

    /// <summary>
    /// Loading a versioned entity then saving a modification should succeed
    /// when no other session has modified the document.
    /// </summary>
    [Test]
    public async Task Concurrency_save_without_conflict_succeeds()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseOptimisticConcurrency = true;
        });
        await using var session = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));

        // Use RawQuery to create an entity with an explicit physical version field.
        const string id = "no_conflict_test";
        await session.Session.RawQuery(
            $"CREATE versioned_person:{id} CONTENT {{ name: 'NoConflict', age: 25, version: 1 }};",
            null);

        // Load via LoadAsync — this auto-tracks the version
        var loaded = await session.LoadAsync<VersionedPerson>(id);
        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("NoConflict");
        loaded.Version.ShouldBe(1);

        // Modify and add as a Modified operation
        loaded.Name = "Updated";
        session.AddOperation(loaded, OperationType.Modified);

        var savedCount = await session.SaveChangesAsync();
        savedCount.ShouldBe(1);
        loaded.Version.ShouldBe(2);
    }

    /// <summary>
    /// When two sessions load the same document and one modifies it first,
    /// the second session's save should throw <see cref="ConcurrencyException"/>.
    /// </summary>
    [Test]
    public async Task Concurrency_conflicting_updates_throw()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseOptimisticConcurrency = true;
        });

        // Arrange: create a base entity with version=1 via RawQuery
        const string id = "conflict_test";
        await using (var seedSession = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })))
        {
            await seedSession.Session.RawQuery(
                $"CREATE versioned_person:{id} CONTENT {{ name: 'ConflictTest', age: 10, version: 1 }};",
                null);
        }

        // Both sessions load the same entity (both track the same version)
        await using var session1 = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));
        await using var session2 = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));

        var entity1 = await session1.LoadAsync<VersionedPerson>(id);
        var entity2 = await session2.LoadAsync<VersionedPerson>(id);
        entity1.ShouldNotBeNull();
        entity2.ShouldNotBeNull();
        entity1.Version.ShouldBe(1);
        entity2.Version.ShouldBe(1);

        // Session2 modifies and saves first (succeeds, version becomes 2)
        entity2.Name = "Session2Wins";
        session2.AddOperation(entity2, OperationType.Modified);
        var session2Count = await session2.SaveChangesAsync();
        session2Count.ShouldBe(1);
        entity2.Version.ShouldBe(2);

        // Session1 tries to modify the same entity (expects version 1, but DB has 2)
        entity1.Name = "Session1TooLate";
        session1.AddOperation(entity1, OperationType.Modified);

        var ex = await Should.ThrowAsync<ConcurrencyException>(async () =>
        {
            await session1.SaveChangesAsync();
        });

        ex.ExpectedVersion.ShouldBe(1);
        ex.ActualVersion.ShouldBe(2);
        ex.DocumentType.ShouldBe(typeof(VersionedPerson));
        ex.Message.ShouldContain("conflict_test");
    }

    /// <summary>
    /// LINQ materialization must capture the original version just like
    /// LoadAsync so Store cannot recreate a document deleted by another session.
    /// </summary>
    [Test]
    public async Task Concurrency_query_loaded_document_deleted_by_another_session_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.UseOptimisticConcurrency = true;
        });

        const string id = "query_delete_conflict";
        await using (var seedSession = await store.OpenSessionAsync(new SessionOptions()))
        {
            seedSession.Store(new VersionedPerson
            {
                Id = new SurrealDb.Net.Models.RecordIdOf<string>("versioned_person", id),
                Name = "Query loaded",
                Age = 30
            });
            await seedSession.SaveChangesAsync();
        }

        await using var staleSession = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });
        await using var deleteSession = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });

        var stale = await staleSession.Query<VersionedPerson>()
            .FirstOrDefaultAsync(person => person.Name == "Query loaded");
        stale.ShouldNotBeNull();

        var current = await deleteSession.LoadAsync<VersionedPerson>(id);
        current.ShouldNotBeNull();
        deleteSession.Delete(current);
        await deleteSession.SaveChangesAsync();

        stale.Name = "Must not be recreated";
        staleSession.Store(stale);

        var exception = await Should.ThrowAsync<ConcurrencyException>(
            () => staleSession.SaveChangesAsync());

        exception.ExpectedVersion.ShouldBe(stale.Version);
        exception.ActualVersion.ShouldBe(0);
    }

    /// <summary>
    /// When optimistic concurrency is disabled, conflicting modifications
    /// should silently succeed (last-write-wins).
    /// </summary>
    [Test]
    public async Task Concurrency_disabled_is_noop()
    {
        await using var store = await TestHarness.CreateStoreAsync(); // UseOptimisticConcurrency = false (default)

        // Arrange: create a base entity with version via RawQuery
        const string id = "no_concurrency_test";
        await using (var seedSession = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })))
        {
            await seedSession.Session.RawQuery(
                $"CREATE versioned_person:{id} CONTENT {{ name: 'NoConcurrency', age: 5, version: 1 }};",
                null);
        }

        // Both sessions load the same entity
        await using var session1 = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));
        await using var session2 = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));

        var entity1 = await session1.LoadAsync<VersionedPerson>(id);
        var entity2 = await session2.LoadAsync<VersionedPerson>(id);
        entity1.ShouldNotBeNull();
        entity2.ShouldNotBeNull();

        // Session2 modifies and saves first
        entity2.Name = "Writer2";
        session2.AddOperation(entity2, OperationType.Modified);
        await session2.SaveChangesAsync();

        // Session1 modifies and saves — should succeed (no concurrency check)
        entity1.Name = "Writer1";
        session1.AddOperation(entity1, OperationType.Modified);

        await session1.SaveChangesAsync();
    }

    /// <summary>
    /// Entities using <see cref="VersionAttribute"/> instead of <see cref="IVersioned"/>
    /// should work identically for concurrency checks.
    /// </summary>
    [Test]
    public async Task Concurrency_version_attribute_works()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseOptimisticConcurrency = true;
        });
        await using var session = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));

        // Use RawQuery to create with the mapped physical version field.
        const string id = "attr_test";
        await session.Session.RawQuery(
            $"CREATE attributed_person:{id} CONTENT {{ name: 'AttrTest', age: 30, document_version: 1 }};",
            null);

        // Load via LoadAsync — auto-tracks version
        var loaded = await session.LoadAsync<AttributedPerson>(id);
        loaded.ShouldNotBeNull();
        loaded.DocumentVersion.ShouldBe(1);

        // Modify and save
        loaded.Name = "AttrUpdated";
        session.AddOperation(loaded, OperationType.Modified);
        await session.SaveChangesAsync();

        loaded.DocumentVersion.ShouldBe(2);
    }

    // ── FetchForWriting tests ─────────────────────────────────────

    [Test]
    public async Task FetchForWriting_AppendOne_SavesSuccessfully()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(new AsyncRebuildableProjection());
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Start a stream
        var streamId = $"ffw-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "FFW-1", Amount = 100m }
        ]);

        // Fetch for writing
        var result = await session.Events.FetchForWritingAsync<WriteModel>(streamId);
        result.ShouldNotBeNull();
        result.Aggregate.ShouldNotBeNull();
        result.ExpectedVersion.ShouldBe(1);

        // Append another event
        result.AppendOne(new OrderEvent { StreamId = streamId, OrderId = "FFW-2", Amount = 50m });
    }

    [Test]
    public async Task FetchForWriting_ConcurrencyConflict_Throws()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var streamId = $"conflict-{Guid.NewGuid():N}";

        // Start stream with first event (version 1)
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "CONF-1", Amount = 100m }
        ]);

        // Fetch for writing — expected version is 1
        var result = await session.Events.FetchForWritingAsync<WriteModel>(streamId);
        result.ExpectedVersion.ShouldBe(1);
        result.Aggregate.ShouldNotBeNull();

        // Another session appends to the stream (making it version 2)
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "CONF-2", Amount = 50m }
        ]);

        // Now try to append using the stale fetch (expected version 1, but actual is 2)
        result.AppendOne(new OrderEvent { StreamId = streamId, OrderId = "CONF-3", Amount = 25m });

        // Appending with expectedVersion 1 should throw since version is now 2
        var ex = await Should.ThrowAsync<ConcurrencyException>(async () =>
        {
            await session.Events.Append(streamId, result.ExpectedVersion, result.PendingEvents);
        });
        ex.ExpectedVersion.ShouldBe(1);
        ex.ActualVersion.ShouldBe(2);
    }

    /// <summary>
    /// Entities without any version tracking (plain <see cref="Person"/>)
    /// should not be affected by optimistic concurrency even when enabled.
    /// </summary>
    [Test]
    public async Task Concurrency_non_versioned_entity_not_affected()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.UseOptimisticConcurrency = true;
        });

        // Create a plain (non-versioned) entity
        const string id = "plain_test";
        await using (var seedSession = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })))
        {
            await seedSession.Session.RawQuery(
                $"CREATE person:{id} CONTENT {{ Name: 'PlainJane', Age: 40 }};",
                null);
        }

        // Load and modify
        await using var session2 = AsDoc(await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }));
        var loaded = await session2.LoadAsync<Person>(id);
        loaded.ShouldNotBeNull();

        loaded.Name = "JaneModified";
        session2.AddOperation(loaded, OperationType.Modified);

        // Should succeed without exception (no version field to check)
        await session2.SaveChangesAsync();
    }

    // ── FetchForWriting auto-flush ───────────────────────────────

    [Test]
    public async Task FetchForWriting_AutoFlush_TracksResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var streamId = $"autoffw-{Guid.NewGuid():N}";

        // Start a stream
        await session.Events.Append(streamId, [
            new OrderEvent { StreamId = streamId, OrderId = "AUTO-1", Amount = 100m }
        ]);

        // Fetch for writing
        var result = await session.Events.FetchForWritingAsync<WriteModel>(streamId);
        result.ExpectedVersion.ShouldBe(1);

        // Append a pending event
        result.AppendOne(new OrderEvent { StreamId = streamId, OrderId = "AUTO-2", Amount = 50m });

        // Verify the result is tracked for auto-flush
        var docSession = (DocumentSession)session;
        docSession._fetchForWritingResults.Count.ShouldBe(1);
        docSession._fetchForWritingResults[0].StreamId.ShouldBe(streamId);
        docSession._fetchForWritingResults[0].ExpectedVersion.ShouldBe(1);
        docSession._fetchForWritingResults[0].PendingEvents.Count.ShouldBe(1);
    }
}
