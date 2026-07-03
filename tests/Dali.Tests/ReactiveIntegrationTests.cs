using System.Reactive.Linq;
using Dali;
using Dali.LiveQuery;
using SurrealDb.Net.Models;

namespace Dali.Tests;

/// <summary>
/// End-to-end integration tests for Dali.Reactive against a real SurrealDB WebSocket server.
/// These tests require a running SurrealDB instance (ws://localhost:8000 by default).
/// They are skipped when no server is available.
/// </summary>
public class ReactiveIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Helper: get a reference to the built query so we can manage its lifecycle
    /// (avoiding reentrancy in builder.ToObservable's own dispose-on-unsubscribe).
    /// </summary>
    private static async Task<(IDocumentStore Store, IDocumentSession WriteSession, ILiveQuerySession LiveSession, IDaliLiveQuery<Person> Query, IObservable<DaliLiveChange<Person>> Observable)> SetupAsync()
    {
        var store = await TestHarnessRemote.CreateStoreAsync();
        var writeSession = await store.OpenSessionAsync(new SessionOptions
        {
            Tracking = DocumentTracking.None
        });
        var liveSession = await store.LiveQuerySessionAsync();
        var builder = liveSession.Live<Person>();
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
        IDaliLiveQuery<Person>? query)
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
        if (!await TestHarnessRemote.IsRemoteAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<Person>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var results = new List<DaliLiveChange<Person>>();
            var tcs = new TaskCompletionSource();

            using var sub = observable.Subscribe(
                results.Add,
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult()
            );

            // Wait for live query connection (Open event and WebSocket handshake)
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Create
            var person = new Person { Name = "Alice", Age = 25, Email = "alice@test.com" };
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
        if (!await TestHarnessRemote.IsRemoteAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<Person>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var results = new List<DaliLiveChange<Person>>();

            using var sub = observable.Subscribe(results.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Perform a write to verify the live query is active
            var person = new Person { Name = "Bob", Age = 30 };
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
        if (!await TestHarnessRemote.IsRemoteAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<Person>? query = null;

        try
        {
            // Arrange — create store and write session
            store = await TestHarnessRemote.CreateStoreAsync();
            writeSession = await store.OpenSessionAsync(new SessionOptions
            {
                Tracking = DocumentTracking.None
            });

            // Store Alice BEFORE the live query exists
            var alice = new Person { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            // Now create the live query session and subscribe
            liveSession = await store.LiveQuerySessionAsync();
            var builder = liveSession.Live<Person>();
            query = await builder.SubscribeAsync();
            var observable = query.ToObservable();

            var results = new List<DaliLiveChange<Person>>();
            using var sub = observable.Subscribe(results.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob AFTER subscribing
            var bob = new Person { Name = "Bob", Age = 35 };
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
        if (!await TestHarnessRemote.IsRemoteAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<Person>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            IDictionary<string, Person>? final = null;
            var tcs = new TaskCompletionSource<bool>();

            using var sub = observable
                .AggregateRecords(new Dictionary<string, Person>())
                .Subscribe(
                    state => final = state,
                    ex => tcs.TrySetException(ex),
                    () => tcs.TrySetResult(true)
                );

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Alice
            var alice = new Person { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob
            var bob = new Person { Name = "Bob", Age = 40 };
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
        if (!await TestHarnessRemote.IsRemoteAvailableAsync())
            return;

        var store = default(IDocumentStore);
        var writeSession = default(IDocumentSession);
        var liveSession = default(ILiveQuerySession);
        IDaliLiveQuery<Person>? query = null;

        try
        {
            // Arrange
            (store, writeSession, liveSession, query, var observable) = await SetupAsync();

            var intermediateStates = new List<IDictionary<string, Person>>();

            using var sub = observable
                .ScanRecords(new Dictionary<string, Person>())
                .Subscribe(intermediateStates.Add);

            // Wait for live query connection
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Alice
            var alice = new Person { Name = "Alice", Age = 25 };
            writeSession.Store(alice);
            await writeSession.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Act — Store Bob
            var bob = new Person { Name = "Bob", Age = 40 };
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
}
