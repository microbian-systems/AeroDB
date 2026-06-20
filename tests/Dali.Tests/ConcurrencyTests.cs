using TUnit.Core;

namespace Dali.Tests;

public class ConcurrencyTests
{
    /// <summary>
    /// Helper to cast <see cref="IDocumentSession"/> to concrete <see cref="DocumentSession"/>
    /// so internal methods like <c>AddOperation</c> are accessible from tests.
    /// </summary>
    private static DocumentSession AsDoc(IDocumentSession s) => (DocumentSession)s;

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
        await using var session = await store.LightweightSessionAsync();

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
        await using var session = AsDoc(await store.LightweightSessionAsync());

        // Use RawQuery to create an entity with an explicit Version field.
        const string id = "no_conflict_test";
        await session.Session.RawQuery(
            $"CREATE versioned_person:{id} CONTENT {{ Name: 'NoConflict', Age: 25, Version: 1 }};",
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
        await using (var seedSession = AsDoc(await store.LightweightSessionAsync()))
        {
            await seedSession.Session.RawQuery(
                $"CREATE versioned_person:{id} CONTENT {{ Name: 'ConflictTest', Age: 10, Version: 1 }};",
                null);
        }

        // Both sessions load the same entity (both track the same version)
        await using var session1 = AsDoc(await store.LightweightSessionAsync());
        await using var session2 = AsDoc(await store.LightweightSessionAsync());

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
    /// When optimistic concurrency is disabled, conflicting modifications
    /// should silently succeed (last-write-wins).
    /// </summary>
    [Test]
    public async Task Concurrency_disabled_is_noop()
    {
        await using var store = await TestHarness.CreateStoreAsync(); // UseOptimisticConcurrency = false (default)

        // Arrange: create a base entity with version via RawQuery
        const string id = "no_concurrency_test";
        await using (var seedSession = AsDoc(await store.LightweightSessionAsync()))
        {
            await seedSession.Session.RawQuery(
                $"CREATE versioned_person:{id} CONTENT {{ Name: 'NoConcurrency', Age: 5, Version: 1 }};",
                null);
        }

        // Both sessions load the same entity
        await using var session1 = AsDoc(await store.LightweightSessionAsync());
        await using var session2 = AsDoc(await store.LightweightSessionAsync());

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
        await using var session = AsDoc(await store.LightweightSessionAsync());

        // Use RawQuery to create with an explicit version field
        const string id = "attr_test";
        await session.Session.RawQuery(
            $"CREATE attributed_person:{id} CONTENT {{ Name: 'AttrTest', Age: 30, DocumentVersion: 1 }};",
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
        await using (var seedSession = AsDoc(await store.LightweightSessionAsync()))
        {
            await seedSession.Session.RawQuery(
                $"CREATE person:{id} CONTENT {{ Name: 'PlainJane', Age: 40 }};",
                null);
        }

        // Load and modify
        await using var session2 = AsDoc(await store.LightweightSessionAsync());
        var loaded = await session2.LoadAsync<Person>(id);
        loaded.ShouldNotBeNull();

        loaded.Name = "JaneModified";
        session2.AddOperation(loaded, OperationType.Modified);

        // Should succeed without exception (no version field to check)
        await session2.SaveChangesAsync();
    }
}
