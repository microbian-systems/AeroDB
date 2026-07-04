using System.Reactive.Linq;
using AeroDB;
using AeroDB.LiveQuery;
using SurrealDb.Net.Models;

namespace AeroDB.IntegrationTests;

/// <summary>
/// End-to-end integration tests for AeroDB.Reactive against a real SurrealDB WebSocket server.
/// These tests require a running SurrealDB instance (ws://localhost:8000 by default).
/// They are skipped when no server is available.
/// </summary>
[Category("Integration")]
public class ReactiveIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Helper: get a reference to the built query so we can manage its lifecycle
    /// (avoiding reentrancy in builder.ToObservable's own dispose-on-unsubscribe).
    /// </summary>
    private static async Task<(IDocumentStore Store, IDocumentSession WriteSession, ILiveQuerySession LiveSession, IDaliLiveQuery<TestPerson> Query, IObservable<DaliLiveChange<TestPerson>> Observable)> SetupAsync()
    {
        var store = await TestHarness.CreateStoreAsync();
        var writeSession = await store.OpenSessionAsync(new SessionOptions
        {
            Tracking = DocumentTracking.None
        });
        var liveSession = await store.LiveQuerySessionAsync();
        var builder = liveSession.Live<TestPerson>();
        var query = await builder.SubscribeAsync();
        var observable = query.ToObservable();

        return (store, writeSession, liveSession, query, observable);
    }

    /// <summary>
    /// Cleanup order: dispose write session, dispose query (stops live query + observable),
    /// dispose live session, dispose store.
    /// </summary>
    private static async Task CleanupAsync(
        IDocumentStore? store,
        IDocumentSession? writeSession,
        ILiveQuerySession? liveSession,
        IDaliLiveQuery<TestPerson>? query)
    {
        if (writeSession is not null)
            await writeSession.DisposeAsync();

        if (query is not null)
            await query.DisposeAsync();

        if (liveSession is not null)
            await liveSession.DisposeAsync();

        if (store is not null)
            await store.DisposeAsync();
    }

    // ── Tests ────────────────────────────────────────────────────

    [Test]
    public async Task Observe_EndToEnd_ReceivesCreateUpdateDelete()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<TestPerson>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var results = new List<DaliLiveChange<TestPerson>>();
            var tcs = new TaskCompletionSource();

            using var sub = observable.Subscribe(
                results.Add,
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult()
            );

            // Wait for live query connection (Open event and WebSocket handshake)
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Create
            var person = new TestPerson { Name = "Alice", Age = 25, Email = "alice@test.com" };
            writeSession.Store(person);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Update
            person.Name = "Alice Updated";
            writeSession.Store(person);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Delete
            writeSession.Delete(person);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Stop the live query so the observable completes
            await query.DisposeAsync();
            query = null;

            // Assert
            results.Count.ShouldBeGreaterThan(1);

            var created = results.FirstOrDefault(r => r.Action == DaliLiveAction.Created);
            created.ShouldNotBeNull();
            created.Document.ShouldNotBeNull();
            created.Document.Name.ShouldBe("Alice");
            created.Document.Email.ShouldBe("alice@test.com");
            created.Document.Age.ShouldBe(25);

            var updated = results.FirstOrDefault(r => r.Action == DaliLiveAction.Updated);
            updated.ShouldNotBeNull();
            updated.Document.ShouldNotBeNull();
            updated.Document.Name.ShouldBe("Alice Updated");

            var deleted = results.FirstOrDefault(r => r.Action == DaliLiveAction.Deleted);
            deleted.ShouldNotBeNull();
        }
        finally
        {
            await CleanupAsync(store, writeSession, liveSession, query);
        }
    }

    [Test]
    public async Task Observe_EndToEnd_EmitsCloseOnDispose()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<TestPerson>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var results = new List<DaliLiveChange<TestPerson>>();

            using var sub = observable.Subscribe(results.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Perform a write to verify the live query is active
            var person = new TestPerson { Name = "Bob", Age = 30 };
            writeSession.Store(person);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — dispose the live query; this should trigger Close on the channel
            await query.DisposeAsync();
            query = null;

            // Allow Close event to propagate
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Assert — at least one Close event should have been received
            results.ShouldContain(r => r.Action == DaliLiveAction.Closed);

            // Also verify we received at least one Created event before close
            results.ShouldContain(r => r.Action == DaliLiveAction.Created);
        }
        finally
        {
            await CleanupAsync(store, writeSession, liveSession, query);
        }
    }

    [Test]
    public async Task Observe_LateSubscriber_OnlyReceivesSubsequentEvents()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<TestPerson>? query = null;

        try
        {
            // Arrange — create store and write session
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });

            // Store Alice BEFORE the live query exists
            var alice = new TestPerson { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            // Now create the live query session and subscribe
            liveSession = await store.LiveQuerySessionAsync();
            var builder = liveSession.Live<TestPerson>();
            query = await builder.SubscribeAsync();
            var observable = query.ToObservable();

            var results = new List<DaliLiveChange<TestPerson>>();
            using var sub = observable.Subscribe(results.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob AFTER subscribing
            var bob = new TestPerson { Name = "Bob", Age = 35 };
            writeSession.Store(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Stop the live query
            await query.DisposeAsync();
            query = null;

            // Assert — Alice (stored before subscription) should NOT be in results
            results.ShouldNotContain(r =>
                r.Action == DaliLiveAction.Created && r.Document != null && r.Document.Name == "Alice");

            // Assert — Bob (stored after subscription) SHOULD be in results
            results.ShouldContain(r =>
                r.Action == DaliLiveAction.Created && r.Document != null && r.Document.Name == "Bob");
        }
        finally
        {
            await CleanupAsync(store, writeSession, liveSession, query);
        }
    }

    [Test]
    public async Task AggregateRecords_EndToEnd()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<TestPerson>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            IDictionary<string, TestPerson>? final = null;
            var tcs = new TaskCompletionSource<bool>();

            using var sub = observable
                .AggregateRecords(new Dictionary<string, TestPerson>())
                .Subscribe(
                    state => final = state,
                    ex => tcs.TrySetException(ex),
                    () => tcs.TrySetResult(true)
                );

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Alice
            var alice = new TestPerson { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob
            var bob = new TestPerson { Name = "Bob", Age = 40 };
            writeSession.Store(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Update Alice (age)
            alice.Age = 30;
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Delete Bob
            writeSession.Delete(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Dispose the live query (completes the observable → triggers aggregate)
            await query.DisposeAsync();
            query = null;

            // Assert — aggregate should have emitted by now
            var completed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            completed.ShouldBeTrue();

            final.ShouldNotBeNull();
            final.Keys.ShouldContain(key => final[key].Name == "Alice");
            final["Alice"].Age.ShouldBe(30);
            final.Keys.ShouldNotContain(key => final[key].Name == "Bob");
        }
        finally
        {
            await CleanupAsync(store, writeSession, liveSession, query);
        }
    }

    [Test]
    public async Task ScanRecords_EndToEnd()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<TestPerson>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var intermediateStates = new List<IDictionary<string, TestPerson>>();

            using var sub = observable
                .ScanRecords(new Dictionary<string, TestPerson>())
                .Subscribe(intermediateStates.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Alice
            var alice = new TestPerson { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob
            var bob = new TestPerson { Name = "Bob", Age = 40 };
            writeSession.Store(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Delete Alice
            writeSession.Delete(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Dispose the live query so the observable completes
            await query.DisposeAsync();
            query = null;

            // Allow final state to propagate
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Assert
            intermediateStates.Count.ShouldBeGreaterThan(1);

            // At least one state should contain Alice
            intermediateStates.ShouldContain(state =>
                state.Values.Any(p => p.Name == "Alice"));

            // The final state should contain Bob but NOT Alice
            var final = intermediateStates[^1];
            final.Values.ShouldContain(p => p.Name == "Bob");
            final.Values.ShouldNotContain(p => p.Name == "Alice");
        }
        finally
        {
            await CleanupAsync(store, writeSession, liveSession, query);
        }
    }

    // ── Fluent-chain integration tests ────────────────────────────

    [Test]
    public async Task FluentChain_Where_ToObservable_SelectCreatedRecords()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Build the fluent chain: Where(p => p.Age > 18) → ToObservable → SelectCreatedRecords
            var obs = liveSession.Live<TestPerson>()
                .Where(p => p.Age > 18)
                .ToObservable()
                .SelectCreatedRecords();

            var createdPersons = new List<TestPerson>();
            using var sub = obs.Subscribe(createdPersons.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store Adult (matches Where predicate) and Child (does NOT match)
            var adult = new TestPerson { Name = "Adult", Age = 25, Email = "adult@test.com" };
            writeSession.Store(adult);
            var child = new TestPerson { Name = "Child", Age = 12, Email = "child@test.com" };
            writeSession.Store(child);
            await writeSession.SaveChangesAsync();

            // Wait for live query events to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup: dispose live session first so the channel closes
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — only the Age=25 Adult should be emitted (Child filtered by the WHERE)
            createdPersons.Count.ShouldBe(1);
            createdPersons[0].Name.ShouldBe("Adult");
            createdPersons[0].Age.ShouldBe(25);
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_CreatedRecords_Shortcut()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Use the CreatedRecords shortcut extension on the builder
            var obs = liveSession.Live<TestPerson>().CreatedRecords();

            var createdPersons = new List<TestPerson>();
            using var sub = obs.Subscribe(createdPersons.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store a Person
            var alice = new TestPerson { Name = "Alice", Age = 30, Email = "alice@test.com" };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();

            // Wait for live query events to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — only the created Person should be received
            createdPersons.Count.ShouldBe(1);
            createdPersons[0].Name.ShouldBe("Alice");
            createdPersons[0].Age.ShouldBe(30);
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_UpdatedRecords_Shortcut()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Use the UpdatedRecords shortcut extension on the builder
            var obs = liveSession.Live<TestPerson>().UpdatedRecords();

            var updatedPersons = new List<TestPerson>();
            using var sub = obs.Subscribe(updatedPersons.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store a Person first
            var person = new TestPerson { Name = "Original Name", Age = 25, Email = "test@test.com" };
            writeSession.Store(person);
            await writeSession.SaveChangesAsync();

            // Wait for the create event to pass through (we don't capture it)
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Update the Person
            person.Name = "Updated Name";
            writeSession.Store(person);
            await writeSession.SaveChangesAsync();

            // Wait for the update event to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — only the update event should be received (UpdatedRecords filters)
            updatedPersons.Count.ShouldBe(1);
            updatedPersons[0].Name.ShouldBe("Updated Name");
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_Results_AggregateRecords()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Build the fluent chain: Results() → AggregateRecords
            var obs = liveSession.Live<TestPerson>()
                .Results()
                .AggregateRecords(new Dictionary<string, TestPerson>());

            IDictionary<string, TestPerson>? finalState = null;
            var tcs = new TaskCompletionSource<bool>();

            using var sub = obs.Subscribe(
                state => finalState = state,
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult(true)
            );

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Alice
            var alice = new TestPerson { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob
            var bob = new TestPerson { Name = "Bob", Age = 40 };
            writeSession.Store(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Update Alice (age)
            alice.Age = 30;
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Delete Bob
            writeSession.Delete(bob);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup: disposing the live session closes the channel → observable completes → Aggregate emits
            await liveSession.DisposeAsync();
            liveSession = null;

            // Wait for AggregateRecords to emit the final state
            var completed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            completed.ShouldBeTrue();

            // Assert — final dictionary should contain Alice (updated Age=30) but NOT Bob
            finalState.ShouldNotBeNull();
            finalState.Values.ShouldContain(p => p.Name == "Alice");
            finalState.Values.First(p => p.Name == "Alice").Age.ShouldBe(30);
            finalState.Values.ShouldNotContain(p => p.Name == "Bob");
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_Where_Results_WithPredicate()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Build the fluent chain: Where(p => p.Name == "Target") → Results()
            var obs = liveSession.Live<TestPerson>()
                .Where(p => p.Name == "Target")
                .Results();

            var allChanges = new List<DaliLiveChange<TestPerson>>();
            using var sub = obs.Subscribe(allChanges.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store "Target" (matches Where predicate) and "Other" (does NOT match)
            var target = new TestPerson { Name = "Target", Age = 30, Email = "target@test.com" };
            writeSession.Store(target);
            var other = new TestPerson { Name = "Other", Age = 25, Email = "other@test.com" };
            writeSession.Store(other);
            await writeSession.SaveChangesAsync();

            // Wait for live query events to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — only the Created event for "Target" should arrive (Other filtered by WHERE)
            allChanges.Count.ShouldBe(1);
            allChanges[0].Action.ShouldBe(DaliLiveAction.Created);
            allChanges[0].Document.ShouldNotBeNull();
            allChanges[0].Document!.Name.ShouldBe("Target");
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_Where_ToObservable_SelectUpdatedRecords()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });
            liveSession = await store.LiveQuerySessionAsync();

            // Build the fluent chain: Where(p => p.Age > 18) → ToObservable → SelectUpdatedRecords
            var obs = liveSession.Live<TestPerson>()
                .Where(p => p.Age > 18)
                .ToObservable()
                .SelectUpdatedRecords();

            var updatedPersons = new List<TestPerson>();
            using var sub = obs.Subscribe(updatedPersons.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store Target (matches Where, Age > 18) and Child (does NOT match)
            var target = new TestPerson { Name = "Target", Age = 25, Email = "target@test.com" };
            writeSession.Store(target);
            var child = new TestPerson { Name = "Child", Age = 12, Email = "child@test.com" };
            writeSession.Store(child);
            await writeSession.SaveChangesAsync();

            // Wait for create events to pass through (we don't capture them with SelectUpdatedRecords)
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Update the Target person
            target.Name = "Target Updated";
            writeSession.Store(target);
            await writeSession.SaveChangesAsync();

            // Wait for the update event to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup: dispose live session first so the channel closes
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — exactly 1 Updated event received (only Target was updated)
            updatedPersons.Count.ShouldBe(1);
            updatedPersons[0].Name.ShouldBe("Target Updated");
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }

    [Test]
    public async Task FluentChain_Where_ToObservable_SelectDeletedRecords()
    {
        if (!await TestHarness.IsAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);

        try
        {
            // Arrange
            store = await TestHarness.CreateStoreAsync();
            writeSession = await store.LightweightSessionAsync();
            liveSession = await store.LiveQuerySessionAsync();

            // Build the fluent chain: Where(p => p.Age > 18) → ToObservable → SelectDeletedRecords
            var obs = liveSession.Live<TestPerson>()
                .Where(p => p.Age > 18)
                .ToObservable()
                .SelectDeletedRecords();

            var deletedPersons = new List<TestPerson>();
            using var sub = obs.Subscribe(deletedPersons.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Act — Store Target (matches Where, Age > 18) and Child (does NOT match)
            var target = new TestPerson { Name = "Target", Age = 25, Email = "target@test.com" };
            writeSession.Store(target);
            var child = new TestPerson { Name = "Child", Age = 12, Email = "child@test.com" };
            writeSession.Store(child);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Delete the Target person
            writeSession.Delete(target);
            await writeSession.SaveChangesAsync();

            // Wait for the delete event to arrive
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Cleanup: dispose live session first so the channel closes
            await liveSession.DisposeAsync();
            liveSession = null;
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // Assert — exactly 1 Deleted event received (only Target was deleted)
            deletedPersons.Count.ShouldBe(1);
            deletedPersons[0].Name.ShouldBe("Target");
            // Child was never deleted — no document should have Name "Child"
            deletedPersons.ShouldNotContain(p => p.Name == "Child");
        }
        finally
        {
            if (writeSession is not null)
                await writeSession.DisposeAsync();
            if (liveSession is not null)
                await liveSession.DisposeAsync();
            if (store is not null)
                await store.DisposeAsync();
        }
    }
}
