using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ─── Test document and event types ─────────────────────────────────

internal sealed class FlatTableDoc : Record
{
    public string Name { get; set; } = "";
    public int Total { get; set; }
    public string Status { get; set; } = "";
}

internal sealed class TableEvent
{
    public string StreamId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

internal sealed class DeleteEvent
{
    public string StreamId { get; set; } = "";
    public bool ShouldDelete { get; set; }
}

internal sealed class CustomEvent
{
    public string StreamId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}

// ─── Tests ─────────────────────────────────────────────────────────

public class FlatTableProjectionTests
{
    [Test]
    public async Task FlatTableProjection_maps_event_to_column()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-1");
        projection.Project<TableEvent>(
            e => e.Name,
            doc => doc.Name);
        projection.Project<TableEvent>(
            e => e.Value,
            doc => doc.Total);

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.Events.StartStream("stream-1", [
            new TableEvent { StreamId = "stream-1", Name = "TestName", Value = 42 }
        ]);
        await session.SaveChangesAsync();

        await using var query = await store.QuerySessionAsync();
        var doc = await query.LoadAsync<FlatTableDoc>("doc-1");
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("TestName");
        doc.Total.ShouldBe(42);
    }

    [Test]
    public async Task FlatTableProjection_accumulates_multiple_events()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-2");
        projection.Project<TableEvent>(
            e => e.Name,
            doc => doc.Name);
        projection.Project<TableEvent>(
            e => e.Value,
            doc => doc.Total);

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.Events.StartStream("stream-2", [
            new TableEvent { StreamId = "stream-2", Name = "First", Value = 10 }
        ]);
        await session.SaveChangesAsync();

        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.StartStream("stream-2", [
            new TableEvent { StreamId = "stream-2", Name = "Second", Value = 20 }
        ]);
        await session2.SaveChangesAsync();

        await using var query = await store.QuerySessionAsync();
        var doc = await query.LoadAsync<FlatTableDoc>("doc-2");
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("Second"); // Last write wins
        doc.Total.ShouldBe(20);
    }

    [Test]
    public async Task FlatTableProjection_uses_custom_value_provider()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-3");
        projection.Set<CustomEvent>(
            doc => doc.Name,
            e => $"{e.FirstName} {e.LastName}");

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.Events.StartStream("stream-3", [
            new CustomEvent { StreamId = "stream-3", FirstName = "John", LastName = "Doe" }
        ]);
        await session.SaveChangesAsync();

        await using var query = await store.QuerySessionAsync();
        var doc = await query.LoadAsync<FlatTableDoc>("doc-3");
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("John Doe");
    }

    [Test]
    public async Task FlatTableProjection_deletes_on_predicate_match()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-4");
        projection.Project<TableEvent>(
            e => e.Name,
            doc => doc.Name);
        projection.Delete<DeleteEvent>(
            e => e.ShouldDelete);

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // First, create the document
        await session.Events.StartStream("stream-4", [
            new TableEvent { StreamId = "stream-4", Name = "ToDelete", Value = 99 }
        ]);
        await session.SaveChangesAsync();

        // Verify it exists
        await using var qs1 = await store.QuerySessionAsync();
        var existing = await qs1.LoadAsync<FlatTableDoc>("doc-4");
        existing.ShouldNotBeNull();
        existing.Name.ShouldBe("ToDelete");

        // Now delete it
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        await session2.Events.StartStream("stream-4", [
            new DeleteEvent { StreamId = "stream-4", ShouldDelete = true }
        ]);
        await session2.SaveChangesAsync();

        // Verify it's gone
        await using var qs2 = await store.QuerySessionAsync();
        var deleted = await qs2.LoadAsync<FlatTableDoc>("doc-4");
        deleted.ShouldBeNull();
    }

    [Test]
    public async Task FlatTableProjection_skips_nonmatching_events()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-5");
        projection.Project<TableEvent>(
            e => e.Name,
            doc => doc.Name);

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Append a non-matching event type (DeleteEvent has no mappings under Project)
        await session.Events.StartStream("stream-5", [
            new DeleteEvent { StreamId = "stream-5", ShouldDelete = false }
        ]);
        await session.SaveChangesAsync();

        // No projection should exist since we used only non-mapped events
        await using var query = await store.QuerySessionAsync();
        var doc = await query.LoadAsync<FlatTableDoc>("doc-5");
        doc.ShouldBeNull();
    }

    [Test]
    public async Task FlatTableProjection_supports_multiple_event_types()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-6");
        projection.Project<TableEvent>(
            e => e.Name,
            doc => doc.Name);
        projection.Project<TableEvent>(
            e => e.Value,
            doc => doc.Total);
        projection.Set<CustomEvent>(
            doc => doc.Status,
            _ => "custom");

        await using var store = await TestHarness.CreateStoreAsync();
        store.Options.Projections.Add(projection);
        store.Options.Events.Enabled = true;
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        await session.Events.StartStream("stream-6", [
            new TableEvent { StreamId = "stream-6", Name = "Multi", Value = 100 },
            new CustomEvent { StreamId = "stream-6", FirstName = "A", LastName = "B" }
        ]);
        await session.SaveChangesAsync();

        await using var query = await store.QuerySessionAsync();
        var doc = await query.LoadAsync<FlatTableDoc>("doc-6");
        doc.ShouldNotBeNull();
        doc.Name.ShouldBe("Multi");
        doc.Total.ShouldBe(100);
        doc.Status.ShouldBe("custom");
    }

    [Test]
    public void FlatTableProjection_exposes_event_types()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-7");
        projection.Project<TableEvent>(e => e.Name, doc => doc.Name);
        projection.Delete<DeleteEvent>(_ => true);
        projection.Set<CustomEvent>(doc => doc.Status, _ => "x");

        var types = projection.EventTypes;
        types.ShouldNotBeNull();
        types.Length.ShouldBe(3);
        types.ShouldContain(typeof(TableEvent));
        types.ShouldContain(typeof(DeleteEvent));
        types.ShouldContain(typeof(CustomEvent));
    }

    [Test]
    public void FlatTableProjection_default_lifecycle_is_inline()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-8");
        projection.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    [Test]
    public void FlatTableProjection_Life_sets_lifecycle()
    {
        var projection = new FlatTableProjection<FlatTableDoc, string>("flat_table",
            events => "doc-9");
        projection.Life(ProjectionLifecycle.Async);
        projection.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }
}
