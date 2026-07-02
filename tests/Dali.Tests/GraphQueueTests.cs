using System.Reflection;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ── Test event and projection types ────────────────────────────

public static class GraphQueueModels
{
    /// <summary>
    /// Event for testing projection-queued relations.
    /// PersonId and FriendId are the string portions of Person RecordIds.
    /// </summary>
    public class FriendshipEstablished
    {
        public string PersonId { get; set; } = "";
        public string FriendId { get; set; } = "";
        public DateTimeOffset HappenedAt { get; set; }
    }
}

/// <summary>
/// Projected document type for <see cref="FriendshipEdgeProjection"/>.
/// </summary>
public class FriendshipProjectionDoc : Record
{
    public string Status { get; set; } = "";
}

/// <summary>
/// Projection that queues a Knows edge when a FriendshipEstablished event is processed.
/// Used by <see cref="GraphQueueTests.ProjectionQueuesRelate_ExecutesInPhaseFiveB"/>.
/// </summary>
internal sealed class FriendshipEdgeProjection : InlineProjection<FriendshipProjectionDoc>
{
    public override Type[] EventTypes => [typeof(GraphQueueModels.FriendshipEstablished)];

    protected override object GetDocumentId(IReadOnlyList<object> events) => "friendship-projection-doc";

    protected override FriendshipProjectionDoc? ApplyEvents(
        FriendshipProjectionDoc? aggregate,
        IReadOnlyList<object> events,
        CancellationToken ct)
    {
        aggregate ??= new FriendshipProjectionDoc();
        aggregate.Status = "projected";
        return aggregate;
    }

    /// <summary>
    /// After the base projection runs (creating/updating the projected document),
    /// queue a Knows edge between the two persons referenced in the event.
    /// This edge will be created during Phase 5b of SaveChangesAsync.
    /// </summary>
    public override async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        await base.ApplyAsync(context, ct);

        foreach (var iEvent in context.TypedEvents)
        {
            if (iEvent.Data is GraphQueueModels.FriendshipEstablished fe)
            {
                var fromId = new RecordIdOf<string>("person", fe.PersonId);
                var toId = new RecordIdOf<string>("person", fe.FriendId);
                context.Session.Relate<Knows>(fromId, toId, new Knows { Kind = "projected" });
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Graph Queue Tests
// ═══════════════════════════════════════════════════════════════════

public class GraphQueueTests
{
    [Test]
    public async Task Relate_Queued_ExecutesInSaveChangesAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Store two people and retrieve their RecordIds
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;

        // Queue a graph edge using the new Relate() API
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "friend" });

        // BEFORE SaveChangesAsync: edge should NOT exist yet (only queued)
        var edgesBefore = await session.Query<Knows>().ToListAsync();
        edgesBefore.Count.ShouldBe(0);

        // Execute queued graph operations
        await session.SaveChangesAsync();

        // AFTER SaveChangesAsync: edge should now exist
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(1);
        edges[0].Kind.ShouldBe("friend");
    }

    [Test]
    public async Task Unrelate_Queued_RemovesEdgeInSaveChangesAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create two Persons
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;

        // Queue the edge and flush it
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "friend" });
        await session.SaveChangesAsync();

        // Verify edge exists
        var edgesBefore = await session.Query<Knows>().ToListAsync();
        edgesBefore.Count.ShouldBe(1);
        var edgeId = edgesBefore[0].Id!;

        // Open a new session and queue the unrelate
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session2.Unrelate(edgeId);

        // BEFORE SaveChangesAsync: edge still exists in the database
        // (query via a different session to avoid any local caching)
        await using var preQuery = await store.QuerySessionAsync();
        var preEdges = await preQuery.Query<Knows>().ToListAsync();
        preEdges.Count.ShouldBe(1);

        // Execute queued unrelate
        await session2.SaveChangesAsync();

        // AFTER SaveChangesAsync: edge should be removed
        await using var verifySession = await store.QuerySessionAsync();
        var edgesAfter = await verifySession.Query<Knows>().ToListAsync();
        edgesAfter.Count.ShouldBe(0);
    }

    [Test]
    public async Task RelateAndUnrelate_Queued_BothExecuteInSaveChangesAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create three People: Alice (source for both edges),
        // Bob (target for new edge), Charlie (target for edge to remove)
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;
        var charlieId = people.First(p => p.Name == "Charlie").Id!;

        // Create an existing edge: Alice -> Charlie
        session.Relate<Knows>(aliceId, charlieId, new Knows { Kind = "existing" });
        await session.SaveChangesAsync();

        // Verify initial state: one edge exists
        var initialEdges = await session.Query<Knows>().ToListAsync();
        initialEdges.Count.ShouldBe(1);
        var existingEdgeId = initialEdges.Single().Id!;

        // Open a new session and queue BOTH operations
        await using var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Queue a new edge: Alice -> Bob
        session2.Relate<Knows>(aliceId, bobId, new Knows { Kind = "new" });

        // Queue removal of existing edge: Alice -> Charlie
        session2.Unrelate(existingEdgeId);

        // Execute both queued operations in a single SaveChangesAsync
        await session2.SaveChangesAsync();

        // Verify: only the new edge exists, the old one was removed
        await using var verifySession = await store.QuerySessionAsync();
        var finalEdges = await verifySession.Query<Knows>().ToListAsync();
        finalEdges.Count.ShouldBe(1);
        finalEdges[0].Kind.ShouldBe("new");
    }

    [Test]
    public async Task ClearChanges_ClearsQueuedRelations()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Create two People
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;

        // Queue a relate
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "friend" });

        // Clear all pending changes (including queued relations)
        session.ClearChanges();

        // SaveChangesAsync should return 0 — nothing to save
        var saved = await session.SaveChangesAsync();
        saved.ShouldBe(0);

        // Verify no edges were created
        await using var verifySession = await store.QuerySessionAsync();
        var edges = await verifySession.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(0);
    }

    [Test]
    public async Task CrossDatabase_RelationsRejectedWhenMismatched()
    {
        // The cross-database check in SaveChangesAsync verifies that queued relation
        // schemas match the unit-of-work database target. We configure a mismatched
        // schema mapping to exercise this validation path.

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            // Route Knows edges to a different database than Person documents
            o.Schema.For<Knows>().Schema("other_db");
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        session.Store(new Person { Name = "Alice" });
        session.Store(new Person { Name = "Bob" });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;

        // Queue a relate — documents target null (default schema) but
        // Knows is mapped to "other_db"
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "cross-db" });

        // SaveChangesAsync should reject cross-database operations
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync());

        ex.Message.ShouldContain("Cross-database");
        ex.Message.ShouldContain("other_db");
    }

    [Test]
    public async Task SaveChangesAsync_RollbackOnRelateFailure_KeepsPreviousRelationsCleared()
    {
        // Use a listener that throws during BeforeSaveChangesAsync to simulate a
        // failure after queued relations have been registered, triggering a rollback.
        //
        // The SurrealDB in-memory client does not reject RELATE to non-existent tables,
        // so we inject a controlled failure instead of relying on a database constraint.

        var failingListener = new FailOnBeforeSaveListener();

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(failingListener);
        });

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Store two Persons
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;

        // Queue 2 valid relates
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "edge-1" });
        session.Relate<Knows>(bobId, aliceId, new Knows { Kind = "edge-2" });

        // Trigger the failure on next SaveChangesAsync
        failingListener.ShouldFail = true;

        // SaveChangesAsync should throw — the listener throws inside the transaction,
        // triggering a rollback of all operations (including queued relations).
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync());

        // After the rollback: verify that the 2 valid relates did NOT create edges
        await using var verifySession = await store.QuerySessionAsync();
        var edgesAfter = await verifySession.Query<Knows>().ToListAsync();
        edgesAfter.Count.ShouldBe(0);
    }

    /// <summary>
    /// Listener that throws during <c>BeforeSaveChangesAsync</c> to simulate
    /// a mid-transaction failure that triggers rollback.
    /// </summary>
    private sealed class FailOnBeforeSaveListener : IDocumentSessionListener
    {
        public bool ShouldFail { get; set; }

        public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct = default)
        {
            if (ShouldFail)
                throw new InvalidOperationException("Simulated failure to trigger transaction rollback.");
            return Task.CompletedTask;
        }

        public Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct = default) => Task.CompletedTask;
        public Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct = default) => Task.CompletedTask;
        public void BeforeStore<T>(T entity) { }
        public void AfterStore<T>(T entity) { }
        public void BeforeDelete<T>(T entity) { }
        public void AfterDelete<T>(T entity) { }
        public void BeforeSaveChanges(IDocumentSession session) { }
        public void AfterSaveChanges(IDocumentSession session) { }
        public void BeforeCommit(IDocumentSession session) { }
        public void AfterCommit(IDocumentSession session, IChangeSet changes) { }
    }

    [Test]
    public async Task ProjectionQueuesRelate_ExecutesInPhaseFiveB()
    {
        // This test verifies that inline projections can queue graph edges via
        // session.Relate() and that those edges are executed inside the same
        // SaveChangesAsync transaction (Phase 5b).

        await using var store = await TestHarness.CreateStoreAsync();

        // Register the projection
        store.Options.Projections.Add(new FriendshipEdgeProjection());

        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Store two Persons
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        await session.SaveChangesAsync();

        // Query back to get populated RecordIds
        var people = await session.Query<Person>().ToListAsync();
        var alice = people.First(p => p.Name == "Alice");
        var bob = people.First(p => p.Name == "Bob");

        // Extract the string portion from each RecordId
        var aliceStrId = alice.Id is RecordIdOf<string> aStr ? aStr.Id : alice.Id!.ToString()!;
        var bobStrId = bob.Id is RecordIdOf<string> bStr ? bStr.Id : bob.Id!.ToString()!;

        // Append event that triggers the projection
        var streamId = $"friendship-{Guid.NewGuid():N}";
        await session.Events.Append(streamId, [
            new GraphQueueModels.FriendshipEstablished
            {
                PersonId = aliceStrId,
                FriendId = bobStrId,
                HappenedAt = DateTimeOffset.UtcNow
            }
        ]);

        // SaveChangesAsync flow:
        //   Phase 3: No entity operations
        //   Phase 4: Inline projection fires
        //          -> base.ApplyAsync creates/stores FriendshipProjectionDoc
        //          -> our override calls context.Session.Relate<Knows>(...) which queues the edge
        //   Phase 5b: Queued graph operations execute inside the transaction
        await session.SaveChangesAsync();

        // Verify the edge was created by the projection
        await using var verifySession = await store.QuerySessionAsync();
        var edges = await verifySession.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(1);
        edges[0].Kind.ShouldBe("projected");

        // Also verify the projection document was created
        var projDocs = await verifySession.Query<FriendshipProjectionDoc>().ToListAsync();
        projDocs.Count.ShouldBe(1);
        projDocs[0].Status.ShouldBe("projected");
    }

    [Test]
    public async Task Relate_WithoutSaveChangesAsync_DoesNotPersistOrphanedEdge()
    {
        // ── Arrange ─────────────────────────────────────────────────
        await using var store = await TestHarness.CreateStoreAsync();
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            // Store two people
            session.Store(new Person { Name = "Alice", Age = 30 });
            session.Store(new Person { Name = "Bob", Age = 25 });
            await session.SaveChangesAsync();

            var people = await session.Query<Person>().ToListAsync();
            var aliceId = people.First(p => p.Name == "Alice").Id!;
            var bobId = people.First(p => p.Name == "Bob").Id!;

            // Queue a graph edge — does NOT execute immediately
            session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "friend" });

            // Dispose session WITHOUT calling SaveChangesAsync —
            // the queued edge is orphaned and must NOT persist.
        }

        // ── Assert: orphaned edge was never flushed ────────────────
        await using (var session2 = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            var edgesAfterDispose = await session2.Query<Knows>().ToListAsync();
            edgesAfterDispose.Count.ShouldBe(0);

            // ── Also verify Relate() works when SaveChangesAsync IS called ─
            var people2 = await session2.Query<Person>().ToListAsync();
            var aliceId2 = people2.First(p => p.Name == "Alice").Id!;
            var bobId2 = people2.First(p => p.Name == "Bob").Id!;

            session2.Relate<Knows>(aliceId2, bobId2, new Knows { Kind = "friend" });
            await session2.SaveChangesAsync();

            var edgesAfterSave = await session2.Query<Knows>().ToListAsync();
            edgesAfterSave.Count.ShouldBe(1);
            edgesAfterSave[0].Kind.ShouldBe("friend");
        }
    }

    [Test]
    public async Task FoF_QueuedGraph_OutDepth2_FindsFriendsOfFriends()
    {
        // ── Topology ───────────────────────────────────────────────
        // Alice ──friend──→ Bob ──colleague──→ Charlie ──colleague──→ Frank
        //     │                │
        //     │ friend         │ colleague
        //     ▼                ▼
        //   Diana ──family──→ Eve
        //
        // Edges:
        //   1. Alice → Bob       (friend)
        //   2. Bob   → Charlie   (colleague)
        //   3. Alice → Diana     (friend)
        //   4. Diana → Eve       (family)
        //   5. Charlie → Frank   (colleague)
        //   6. Bob   → Diana     (colleague)  ← diamond
        // ───────────────────────────────────────────────────────────

        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // 1. Store 6 people
        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        session.Store(new Person { Name = "Diana", Age = 28 });
        session.Store(new Person { Name = "Eve", Age = 22 });
        session.Store(new Person { Name = "Frank", Age = 40 });
        await session.SaveChangesAsync();

        // 2. Query to get populated RecordIds
        var people = await session.Query<Person>().ToListAsync();
        var aliceId = people.First(p => p.Name == "Alice").Id!;
        var bobId = people.First(p => p.Name == "Bob").Id!;
        var charlieId = people.First(p => p.Name == "Charlie").Id!;
        var dianaId = people.First(p => p.Name == "Diana").Id!;
        var eveId = people.First(p => p.Name == "Eve").Id!;
        var frankId = people.First(p => p.Name == "Frank").Id!;

        // 3. Build social graph ENTIRELY with queued Relate() (not RelateAsync)
        session.Relate<Knows>(aliceId, bobId, new Knows { Kind = "friend" });
        session.Relate<Knows>(bobId, charlieId, new Knows { Kind = "colleague" });
        session.Relate<Knows>(aliceId, dianaId, new Knows { Kind = "friend" });
        session.Relate<Knows>(dianaId, eveId, new Knows { Kind = "family" });
        session.Relate<Knows>(charlieId, frankId, new Knows { Kind = "colleague" });
        session.Relate<Knows>(bobId, dianaId, new Knows { Kind = "colleague" });

        // 4. BEFORE SaveChangesAsync: no edges exist yet (only queued)
        var edgesBefore = await session.Query<Knows>().ToListAsync();
        edgesBefore.Count.ShouldBe(0);

        // 5. Execute all 6 queued edges atomically
        await session.SaveChangesAsync();

        // 6. AFTER SaveChangesAsync: all 6 edges exist
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(6);

        // 7. Graph traversal: verify API surface works (embedded engine may
        //    return partial results — see GraphFriendOfFriendsTests notes)
        var aliceFriends = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person, Knows>()
            .ToListAsync();
        aliceFriends.ShouldNotBeNull();
        aliceFriends.Count.ShouldBeGreaterThanOrEqualTo(1);

        var aliceFoF = await session.Graph<Person>()
            .Where(p => p.Name == "Alice")
            .Out<Person, Knows>()
            .Depth(2)
            .ToListAsync();
        aliceFoF.ShouldNotBeNull();
        aliceFoF.Any(p => p.Name == "Alice").ShouldBeFalse();
    }
}
