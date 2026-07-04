using System.IO;
using AeroDB;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

// ─── Test POCOs for document storage tests ──────────────────────────

public class AdvSimpleDoc : Record
{
    public string Name { get; set; } = "";
}

public class AdvOtherDoc : Record
{
    public int Value { get; set; }
}

public class AdvThirdDoc : Record
{
    public bool Flag { get; set; }
}

public class QueueTestDoc : Record
{
    public string Value { get; set; } = "";
}

// ─── DaliAdvanced operations tests ─────────────────────────────────

/// <summary>
/// Tests for <see cref="IDaliAdvanced"/> operations exposed via <c>store.Advanced</c>.
/// Each test creates its own isolated store (unique namespace) so they do not share
/// database state.
/// </summary>
[NotInParallel]
public class DaliAdvancedOperationsTests
{
    private static async Task SeedAsync<T>(IDocumentStore store, params T[] docs) where T : class
    {
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        foreach (var doc in docs)
            session.Store(doc);
        await session.SaveChangesAsync();
    }

    private static async Task<int> CountAsync<T>(IDocumentStore store) where T : Record
    {
        await using var query = await store.QuerySessionAsync();
        return (await query.Query<T>().ToListAsync()).Count;
    }

    // ── ResetAllDataAsync ───────────────────────────────────────────

    [Test]
    public async Task ResetAllDataAsync_ClearsAllTables()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>();
            o.Schema.For<AdvOtherDoc>();
        });

        await SeedAsync(store,
            new AdvSimpleDoc { Name = "Alpha" },
            new AdvSimpleDoc { Name = "Beta" },
            new AdvSimpleDoc { Name = "Gamma" });

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(3);

        await store.Advanced.ResetAllDataAsync();

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(0);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(0);
    }

    // ── DeleteAllDocumentsAsync<T> ──────────────────────────────────

    [Test]
    public async Task DeleteAllDocumentsAsync_RemovesAllOfType()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>();
            o.Schema.For<AdvOtherDoc>();
        });

        await SeedAsync(store,
            new AdvSimpleDoc { Name = "S1" },
            new AdvSimpleDoc { Name = "S2" },
            new AdvSimpleDoc { Name = "S3" });
        await SeedAsync(store,
            new AdvOtherDoc { Value = 10 },
            new AdvOtherDoc { Value = 20 },
            new AdvOtherDoc { Value = 30 });

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(3);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(3);

        await store.Advanced.DeleteAllDocumentsAsync<AdvSimpleDoc>();

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(0);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(3);
    }

    // ── DeleteDocumentsByTypeAsync ──────────────────────────────────

    [Test]
    public async Task DeleteDocumentsByTypeAsync_RemovesSpecifiedType()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>();
            o.Schema.For<AdvOtherDoc>();
        });

        await SeedAsync(store,
            new AdvSimpleDoc { Name = "DeleteMe" },
            new AdvSimpleDoc { Name = "MeToo" });
        await SeedAsync(store,
            new AdvOtherDoc { Value = 100 },
            new AdvOtherDoc { Value = 200 });

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(2);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(2);

        await store.Advanced.DeleteDocumentsByTypeAsync(typeof(AdvSimpleDoc));

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(0);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(2);
    }

    // ── DeleteDocumentsExceptAsync ──────────────────────────────────

    [Test]
    public async Task DeleteDocumentsExceptAsync_PreservesSpecifiedTypes()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>();
            o.Schema.For<AdvOtherDoc>();
            o.Schema.For<AdvThirdDoc>();
        });

        await SeedAsync(store, new AdvSimpleDoc { Name = "Keep1" });
        await SeedAsync(store, new AdvOtherDoc { Value = 42 });
        await SeedAsync(store, new AdvThirdDoc { Flag = true });

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(1);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(1);
        (await CountAsync<AdvThirdDoc>(store)).ShouldBe(1);

        // Preserve AdvSimpleDoc and AdvOtherDoc → should delete AdvThirdDoc only
        await store.Advanced.DeleteDocumentsExceptAsync([typeof(AdvSimpleDoc), typeof(AdvOtherDoc)]);

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(1);
        (await CountAsync<AdvOtherDoc>(store)).ShouldBe(1);
        (await CountAsync<AdvThirdDoc>(store)).ShouldBe(0);
    }

    // ── CompletelyRemoveAsync ───────────────────────────────────────

    [Test]
    public async Task CompletelyRemoveAsync_DropsTable()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>();
        });

        await SeedAsync(store, new AdvSimpleDoc { Name = "RemoveMe" });

        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(1);

        await store.Advanced.CompletelyRemoveAsync(typeof(AdvSimpleDoc));

        // After REMOVE TABLE the table no longer exists → queries return 0 rows
        (await CountAsync<AdvSimpleDoc>(store)).ShouldBe(0);
    }

    // ── DeleteAllEventDataAsync ─────────────────────────────────────

    [Test]
    public async Task DeleteAllEventDataAsync_RemovesEventData()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var streamId = $"daevt-{Guid.NewGuid():N}";
        await session.Events.StartStream(streamId,
        [
            new OrderEvent { StreamId = streamId, OrderId = "EVT-DEL-1", Amount = 100m }
        ]);

        var events = await session.Events.FetchStream(streamId);
        events.Count.ShouldBe(1);

        // Act
        await store.Advanced.DeleteAllEventDataAsync();

        // Assert — all rows in mt_events are gone
        var eventsAfter = await session.Events.FetchStream(streamId);
        eventsAfter.Count.ShouldBe(0);
    }

    // ── ComputeSchemaDiffAsync ──────────────────────────────────────

    [Test]
    public async Task ComputeSchemaDiffAsync_ReturnsDiff()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<AdvSimpleDoc>().SetSchemaMode(SchemaMode.Flexible);
        });

        var diff = await store.Advanced.ComputeSchemaDiffAsync();

        diff.ShouldNotBeNull();
        diff.Differences.ShouldNotBeNull();
        // The diff may have differences if the INFO FOR TABLE response includes
        // extra system fields not in the configured mapping, or vice versa.
        // We just verify the method executed without throwing.
    }

    [Test]
    public async Task ComputeSchemaDiffAsync_WithNoMappings_ReturnsEmptyDiff()
    {
        // No types registered in Schema.Mappings
        await using var store = await TestHarness.CreateStoreAsync();

        var diff = await store.Advanced.ComputeSchemaDiffAsync();

        diff.ShouldNotBeNull();
        diff.Differences.ShouldBeEmpty();
        diff.HasChanges.ShouldBeFalse();
    }

    // ── StreamJson ──────────────────────────────────────────────────

    [Test]
    public async Task StreamJson_WritesQueryResultsAsJson()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<Person>();
        });

        await SeedAsync(store, new Person { Name = "Stream Person", Age = 42 });

        await using var query = await store.QuerySessionAsync();
        using var stream = new MemoryStream();

        await query.StreamJson<Person>(stream, "SELECT * FROM person", parameters: null);

        stream.Position = 0;
        var json = new StreamReader(stream).ReadToEnd();

        json.ShouldContain("Stream Person");
        json.ShouldContain("42");
    }

    [Test]
    public async Task StreamJson_WithPlaceholder_WritesJsonToStream()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<Person>();
        });

        await SeedAsync(store, new Person { Name = "Placeholder Person", Age = 99 });

        await using var query = await store.QuerySessionAsync();
        using var stream = new MemoryStream();

        // The placeholder is replaced with the database name; if it doesn't appear
        // in the SQL, the replacement is a no-op. The overload still functions correctly.
        await query.StreamJson<Person>(stream, "{database}", "SELECT * FROM person", parameters: null);

        stream.Position = 0;
        var json = new StreamReader(stream).ReadToEnd();

        json.ShouldContain("Placeholder Person");
        json.ShouldContain("99");
    }

    // ── QueueSqlCommand ─────────────────────────────────────────────

    [Test]
    public async Task QueueSqlCommand_ExecutesDuringSaveChanges()
    {
        await using var store = await TestHarness.CreateStoreAsync();

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Note: SurrealDB in-memory engine uses CBOR with PascalCase field names,
        // so the field name must match the C# property name exactly.
        session.QueueSqlCommand("{database}", "CREATE queue_test_doc SET Value = $p0", "queued-value");
        await session.SaveChangesAsync();

        // Verify via RawQueryAsync (bypasses any LINQ-level field mapping)
        await using var query = await store.QuerySessionAsync();
        var results = await query.RawQueryAsync<QueueTestDoc>("SELECT * FROM queue_test_doc");

        results.Count.ShouldBe(1);
        results[0].Value.ShouldBe("queued-value");
    }
}
