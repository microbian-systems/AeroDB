using AeroDB.Sable;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests.Poco;

// ─── POCO Types ───────────────────────────────────────────────────

public class PocoPerson
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public class PocoBook
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
}

public class PocoStringPerson
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class PocoStringBook
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

// ─── Edge Types (AeroDB.Sable types, nodes are POCOs) ────────────────────

public class PocoWrote : EdgeRecord { }
public class PocoReviewed : EdgeRecord { }
public class PocoStringWrote : EdgeRecord { }

// ─── POCO Graph CRUD Tests ───────────────────────────────────────

public class PocoGraphTests
{
    [Test]
    public async Task PocoGraph_StringIdentity_TraverseAndDeduplicate()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoStringPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoStringBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoStringPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoStringBook));

        session.Store(new PocoStringPerson { Id = "alice", Name = "Alice" });
        session.Store(new PocoStringBook { Id = "book-1", Title = "String IDs" });
        await session.SaveChangesAsync();

        session.Relate<PocoStringWrote>(
            new RecordIdOf<string>(personTable, "alice"),
            new RecordIdOf<string>(bookTable, "book-1"));
        await session.SaveChangesAsync();

        var results = await session.Graph<PocoStringPerson>()
            .Where(p => p.Id == "alice")
            .Out<PocoStringBook>("poco_string_wrote")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("book-1");
        results[0].Title.ShouldBe("String IDs");
    }

    [Test]
    public async Task PocoGraph_MultipleRelationships_ToSingleNode_AndDelete()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Create 3 PocoPersons (A, B, C) and 1 PocoBook (X)
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var personB = new PocoPerson { Id = 2, Name = "B" };
        var personC = new PocoPerson { Id = 3, Name = "C" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(personB);
        session.Store(personC);
        session.Store(bookX);
        await session.SaveChangesAsync();

        // Relate A→X via PocoWrote, B→X via PocoWrote, C→X via PocoReviewed
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personB.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        session.Relate<PocoReviewed>(
            new RecordIdOf<long>(personTable, personC.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // ── Verify graph traversal ────────────────────────────────

        // In<PocoPerson>("poco_wrote") from X → 2 persons (A, B)
        var wroteResults = await session.Graph<PocoBook>()
            .Where(p => p.Title == "X")
            .In<PocoPerson>("poco_wrote")
            .ToListAsync();
        wroteResults.Count.ShouldBe(2);
        wroteResults.Select(p => p.Name).OrderBy(n => n).ShouldBe(["A", "B"]);

        // In<PocoPerson>("poco_wrote") count via CountAsync
        var wroteCount = await session.Graph<PocoBook>()
            .Where(p => p.Title == "X")
            .In<PocoPerson>("poco_wrote")
            .CountAsync();
        wroteCount.ShouldBe(2);

        // In<PocoPerson>("poco_reviewed") from X → 1 person (C)
        var reviewedResults = await session.Graph<PocoBook>()
            .Where(p => p.Title == "X")
            .In<PocoPerson>("poco_reviewed")
            .ToListAsync();
        reviewedResults.Count.ShouldBe(1);
        reviewedResults[0].Name.ShouldBe("C");

        // ── Delete book X ──────────────────────────────────────────
        var loadedBook = await session.LoadAsync<PocoBook>(bookX.Id.ToString());
        loadedBook.ShouldNotBeNull();
        session.Delete(loadedBook);
        await session.SaveChangesAsync();

        // ── Verify edges are cleaned up ────────────────────────────
        var wroteEdges = await session.Query<PocoWrote>().ToListAsync();
        wroteEdges.Count.ShouldBe(0);

        var reviewedEdges = await session.Query<PocoReviewed>().ToListAsync();
        reviewedEdges.Count.ShouldBe(0);

        // ── Verify the 3 persons still exist ───────────────────────
        var loadedA = await session.LoadAsync<PocoPerson>(personA.Id.ToString());
        loadedA.ShouldNotBeNull();
        loadedA.Name.ShouldBe("A");

        var loadedB = await session.LoadAsync<PocoPerson>(personB.Id.ToString());
        loadedB.ShouldNotBeNull();
        loadedB.Name.ShouldBe("B");

        var loadedC = await session.LoadAsync<PocoPerson>(personC.Id.ToString());
        loadedC.ShouldNotBeNull();
        loadedC.Name.ShouldBe("C");

        // ── Verify book X is gone ──────────────────────────────────
        var loadedBookAgain = await session.LoadAsync<PocoBook>(bookX.Id.ToString());
        loadedBookAgain.ShouldBeNull();
    }

    [Test]
    public async Task PocoGraph_OutTraversal_ReturnsCorrectNodes()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Store 2 PocoPersons (A, B) and 1 PocoBook (X)
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var personB = new PocoPerson { Id = 2, Name = "B" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(personB);
        session.Store(bookX);
        await session.SaveChangesAsync();

        // Relate A→X via PocoWrote, B→X via PocoWrote
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personB.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // Verify: Out traversal from A → 1 result (X)
        var results = await session.Graph<PocoPerson>()
            .Where(p => p.Name == "A")
            .Out<PocoBook>("poco_wrote")
            .ToListAsync();

        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("X");
    }

    [Test]
    public async Task PocoGraph_InTraversal_ReturnsCorrectNodes()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Store 2 PocoPersons (A, B) and 1 PocoBook (X)
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var personB = new PocoPerson { Id = 2, Name = "B" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(personB);
        session.Store(bookX);
        await session.SaveChangesAsync();

        // Relate A→X via PocoWrote, B→X via PocoWrote
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personB.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // Verify: In traversal from X → 2 results (A, B)
        var results = await session.Graph<PocoBook>()
            .Where(p => p.Title == "X")
            .In<PocoPerson>("poco_wrote")
            .ToListAsync();

        results.Count.ShouldBe(2);
        results.Select(p => p.Name).OrderBy(n => n).ShouldBe(["A", "B"]);
    }

    [Test]
    public async Task PocoGraph_Relate_CreatesEdgeRecord()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Store 1 PocoPerson (A) and 1 PocoBook (X)
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(bookX);
        await session.SaveChangesAsync();

        // Relate A→X via PocoWrote
        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // Verify: Query<PocoWrote>() → 1 result
        var edges = await session.Query<PocoWrote>().ToListAsync();
        edges.Count.ShouldBe(1);
        edges[0].In.ShouldNotBeNull();
        edges[0].Out.ShouldNotBeNull();
    }

    [Test]
    public async Task PocoGraph_Delete_ClearsOutgoingEdges()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Create PocoPerson A and PocoBook X, relate A→X
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(bookX);
        await session.SaveChangesAsync();

        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // Delete PocoPerson A (the source node)
        session.Delete<PocoPerson>(personA.Id.ToString());
        await session.SaveChangesAsync();

        // Verify edges are cleaned up
        var edges = await session.Query<PocoWrote>().ToListAsync();
        edges.Count.ShouldBe(0);
    }

    [Test]
    public async Task PocoGraph_Delete_ClearsIncomingEdges()
    {
        await using var store = await TestHarness.CreateStoreAsync(opts =>
        {
            opts.Schema.For<PocoPerson>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Schema.For<PocoBook>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
        });
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var personTable = MetadataDispatch.GetTableName(typeof(PocoPerson));
        var bookTable = MetadataDispatch.GetTableName(typeof(PocoBook));

        // Create PocoPerson A and PocoBook X, relate A→X
        var personA = new PocoPerson { Id = 1, Name = "A" };
        var bookX   = new PocoBook  { Id = 10, Title = "X" };

        session.Store(personA);
        session.Store(bookX);
        await session.SaveChangesAsync();

        session.Relate<PocoWrote>(
            new RecordIdOf<long>(personTable, personA.Id),
            new RecordIdOf<long>(bookTable, bookX.Id));
        await session.SaveChangesAsync();

        // Delete PocoBook X (the target node)
        session.Delete<PocoBook>(bookX.Id.ToString());
        await session.SaveChangesAsync();

        // Verify edges are cleaned up
        var edges = await session.Query<PocoWrote>().ToListAsync();
        edges.Count.ShouldBe(0);
    }
}
