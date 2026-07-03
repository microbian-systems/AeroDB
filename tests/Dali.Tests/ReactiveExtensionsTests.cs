using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dali.LiveQuery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.LiveQuery;
using NSubstitute;
using TUnit.Core;

namespace Dali.Tests;

public class ReactiveExtensionsTests
{
    // ── Helper methods ──────────────────────────────────────────

    /// <summary>
    /// Extracts a meaningful string key from a Person's RecordId.
    /// RecordId does not override ToString(), so we use the typed Id property
    /// of RecordIdOf{string} when available.
    /// </summary>
    private static string RecordKey(Person p) =>
        p.Id is RecordIdOf<string> rid ? rid.Id : p.Id?.ToString() ?? "";

    private static DaliLiveChange<Person> CreateOpen() =>
        new(DaliLiveAction.Open, null, null);

    private static DaliLiveChange<Person> CreateCreated(Person p) =>
        new(DaliLiveAction.Created, RecordKey(p), p);

    private static DaliLiveChange<Person> CreateUpdated(Person p) =>
        new(DaliLiveAction.Updated, RecordKey(p), p);

    private static DaliLiveChange<Person> CreateDeleted(Person p) =>
        new(DaliLiveAction.Deleted, RecordKey(p), p);

    private static DaliLiveChange<Person> CreateClosed(
        SurrealDbLiveQueryClosureReason reason = SurrealDbLiveQueryClosureReason.QueryKilled) =>
        new(DaliLiveAction.Closed, null, null, reason);

    /// <summary>Subscribes to an observable, collects all items, and returns them.</summary>
    private static async Task<List<T>> ToListAsync<T>(IObservable<T> observable)
    {
        var tcs = new TaskCompletionSource<bool>();
        var results = new List<T>();
        observable.Subscribe(
            results.Add,
            ex => tcs.TrySetException(ex),
            () => tcs.TrySetResult(true));
        await tcs.Task;
        return results;
    }

    // ── State Accumulator Tests (pure Rx, no mocking) ───────────

    [Test]
    public async Task AggregateRecords_AccumulatesCreateUpdateDelete()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var aliceOlder = new Person { Id = RecordId.From("person", "alice"), Name = "Alice (older)" };

        var source = new[]
        {
            CreateOpen(),
            CreateCreated(alice),
            CreateUpdated(aliceOlder),
            CreateDeleted(alice),
            CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(
            source.AggregateRecords(new Dictionary<string, Person>()));

        results.Count.ShouldBe(1);
        results[0].ShouldBeEmpty();
    }

    [Test]
    public async Task ScanRecords_EmitsAfterEachEvent()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };

        var source = new[] { CreateCreated(alice), CreateCreated(bob) }.ToObservable();

        var results = await ToListAsync(
            source.ScanRecords(new Dictionary<string, Person>()));

        results.Count.ShouldBe(2);
        results[0].Values.ShouldContain(p => p.Name == "Alice");
        results[1].Values.ShouldContain(p => p.Name == "Alice");
        results[1].Values.ShouldContain(p => p.Name == "Bob");
    }

    [Test]
    public async Task ScanRecords_DeleteRemovesFromAccumulator()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };

        var source = new[] { CreateCreated(alice), CreateCreated(bob), CreateDeleted(alice) }.ToObservable();

        var results = await ToListAsync(
            source.ScanRecords(new Dictionary<string, Person>()));

        results.Count.ShouldBe(3);
        var final = results[^1];
        final.Values.ShouldContain(p => p.Name == "Bob");
        final.Values.ShouldNotContain(p => p.Name == "Alice");
    }

    // ── LINQ Filter Tests (pure Rx) ────────────────────────────

    [Test]
    public async Task SelectCreatedRecords_FiltersOnlyCreated()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectCreatedRecords());

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task SelectUpdatedRecords_FiltersOnlyUpdated()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectUpdatedRecords());

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task SelectDeletedRecords_FiltersOnlyDeleted()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectDeletedRecords());

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task SelectResults_ExcludesClose()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };

        var source = new[] { CreateOpen(), CreateCreated(alice), CreateClosed() }.ToObservable();

        var results = await ToListAsync(source.SelectResults());

        results.Count.ShouldBe(2);
        results[0].Action.ShouldBe(DaliLiveAction.Open);
        results[1].Action.ShouldBe(DaliLiveAction.Created);
    }

    // ── Builder ToObservable Tests (need mocking) ───────────────

    [Test]
    public async Task ToObservable_Builder_ForwardsAllChanges()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var aliceOlder = new Person { Id = RecordId.From("person", "alice"), Name = "Alice (older)" };

        var query = Substitute.For<IDaliLiveQuery<Person>>();
        var builder = Substitute.For<IDaliLiveQueryBuilder<Person>>();

        builder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(query);

        var changes = new[] { CreateOpen(), CreateCreated(alice), CreateUpdated(aliceOlder), CreateClosed() };
        query.Changes(Arg.Any<CancellationToken>()).Returns(MakeAsyncEnumerable(changes));

        var observable = builder.ToObservable();
        var results = await ToListAsync(observable);

        results.Count.ShouldBe(4);
        results[0].Action.ShouldBe(DaliLiveAction.Open);
        results[1].Action.ShouldBe(DaliLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(DaliLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Alice (older)");
        results[3].Action.ShouldBe(DaliLiveAction.Closed);
    }

    [Test]
    public async Task ToObservable_Builder_DisposesOnUnsubscribe()
    {
        var query = Substitute.For<IDaliLiveQuery<Person>>();
        var builder = Substitute.For<IDaliLiveQueryBuilder<Person>>();

        builder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(query);

        query.Changes(Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var ct = callInfo.Arg<CancellationToken>();
            return MakeInfiniteEnumerable(ct);
        });

        var observable = builder.ToObservable();
        var subscription = observable.Subscribe(_ => { });

        // Allow the subscription to start and yield the first event
        await Task.Delay(200);

        subscription.Dispose();

        // Allow async disposal to complete
        await Task.Delay(500);

        await query.Received(1).DisposeAsync();
    }

    // ── Direct Query ToObservable Tests (need mocking) ─────────

    [Test]
    public async Task ToObservable_Query_ForwardsChanges()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var aliceOlder = new Person { Id = RecordId.From("person", "alice"), Name = "Alice (older)" };

        var query = Substitute.For<IDaliLiveQuery<Person>>();

        var changes = new[] { CreateCreated(alice), CreateUpdated(aliceOlder) };
        query.Changes(Arg.Any<CancellationToken>()).Returns(MakeAsyncEnumerable(changes));

        var observable = query.ToObservable();
        var results = await ToListAsync(observable);

        results.Count.ShouldBe(2);
        results[0].Action.ShouldBe(DaliLiveAction.Created);
        results[0].Document!.Name.ShouldBe("Alice");
        results[1].Action.ShouldBe(DaliLiveAction.Updated);
        results[1].Document!.Name.ShouldBe("Alice (older)");
    }

    [Test]
    public async Task ToObservable_Query_DoesNotDisposeOnUnsubscribe()
    {
        var query = Substitute.For<IDaliLiveQuery<Person>>();

        query.Changes(Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var ct = callInfo.Arg<CancellationToken>();
            return MakeInfiniteEnumerable(ct);
        });

        var observable = query.ToObservable();
        var subscription = observable.Subscribe(_ => { });

        await Task.Delay(200);

        subscription.Dispose();
        await Task.Delay(500);

        await query.DidNotReceive().DisposeAsync();
    }

    private static async IAsyncEnumerable<DaliLiveChange<Person>> MakeAsyncEnumerable(
        DaliLiveChange<Person>[] changes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var c in changes)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
    }

    private static async IAsyncEnumerable<DaliLiveChange<Person>> MakeInfiniteEnumerable(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateOpen();

        // Block until cancellation is requested
        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }

    // ── SurrealDB LiveQuery Response Helpers ──────────────────────

    private static readonly ILogger<SurrealDaliLiveQuery<Person>> NullPersonLogger
        = NullLogger<SurrealDaliLiveQuery<Person>>.Instance;

    private static async IAsyncEnumerable<SurrealDbLiveQueryResponse> SingleResponse(
        SurrealDbLiveQueryResponse response,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateSdkOpen();
        yield return response;
        yield return CreateSdkClose();
    }

    private static async IAsyncEnumerable<SurrealDbLiveQueryResponse> MultipleResponses(
        SurrealDbLiveQueryResponse[] responses,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateSdkOpen();
        foreach (var r in responses)
            yield return r;
        yield return CreateSdkClose();
    }

    private static SurrealDbLiveQueryOpenResponse CreateSdkOpen() =>
        (SurrealDbLiveQueryOpenResponse)Activator.CreateInstance(
            typeof(SurrealDbLiveQueryOpenResponse), nonPublic: true)!;

    private static SurrealDbLiveQueryCloseResponse CreateSdkClose(
        SurrealDbLiveQueryClosureReason reason = SurrealDbLiveQueryClosureReason.QueryKilled) =>
        (SurrealDbLiveQueryCloseResponse)Activator.CreateInstance(
            typeof(SurrealDbLiveQueryCloseResponse),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { reason }, null)!;

    private static SurrealDbLiveQueryCreateResponse<T> CreateSdkCreate<T>(T r) =>
        (SurrealDbLiveQueryCreateResponse<T>)Activator.CreateInstance(
            typeof(SurrealDbLiveQueryCreateResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { r! }, null)!;

    private static SurrealDbLiveQueryUpdateResponse<T> CreateSdkUpdate<T>(T r) =>
        (SurrealDbLiveQueryUpdateResponse<T>)Activator.CreateInstance(
            typeof(SurrealDbLiveQueryUpdateResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { r! }, null)!;

    private static SurrealDbLiveQueryDeleteResponse<T> CreateSdkDelete<T>(T r) =>
        (SurrealDbLiveQueryDeleteResponse<T>)Activator.CreateInstance(
            typeof(SurrealDbLiveQueryDeleteResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { r! }, null)!;

    // ── IDaliLiveQuery<T> Filter Method Tests ────────────────────

    [Test]
    public async Task GetResults_FiltersOutClose()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var aliceUpdated = new Person { Id = RecordId.From("person", "alice"), Name = "Alice Updated" };

        var source = MultipleResponses(new SurrealDbLiveQueryResponse[]
        {
            CreateSdkCreate(alice),
            CreateSdkUpdate(aliceUpdated),
            CreateSdkDelete(alice),
        });
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<DaliLiveChange<Person>>();
        await foreach (var c in adapter.GetResults())
        {
            results.Add(c);
        }

        results.Count.ShouldBe(4); // Open + Created + Updated + Deleted (no Close)
        results[0].Action.ShouldBe(DaliLiveAction.Open);
        results[1].Action.ShouldBe(DaliLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(DaliLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Alice Updated");
        results[3].Action.ShouldBe(DaliLiveAction.Deleted);
    }

    [Test]
    public async Task GetCreatedRecords_ReturnsOnlyCreatedDocuments()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = MultipleResponses(new SurrealDbLiveQueryResponse[]
        {
            CreateSdkCreate(alice),
            CreateSdkUpdate(bob),
            CreateSdkDelete(charlie),
        });
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<Person>();
        await foreach (var doc in adapter.GetCreatedRecords())
        {
            results.Add(doc);
        }

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task GetUpdatedRecords_ReturnsOnlyUpdatedDocuments()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = MultipleResponses(new SurrealDbLiveQueryResponse[]
        {
            CreateSdkCreate(alice),
            CreateSdkUpdate(bob),
            CreateSdkDelete(charlie),
        });
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<Person>();
        await foreach (var doc in adapter.GetUpdatedRecords())
        {
            results.Add(doc);
        }

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task GetDeletedRecords_ReturnsOnlyDeletedDocuments()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = MultipleResponses(new SurrealDbLiveQueryResponse[]
        {
            CreateSdkCreate(alice),
            CreateSdkUpdate(bob),
            CreateSdkDelete(charlie),
        });
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<Person>();
        await foreach (var doc in adapter.GetDeletedRecords())
        {
            results.Add(doc);
        }

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task GetResults_WhenOnlyOpenAndClose_ReturnsOnlyOpen()
    {
        var source = SingleResponse(CreateSdkCreate(new Person { Id = RecordId.From("person", "x"), Name = "X" }));
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<DaliLiveChange<Person>>();
        await foreach (var c in adapter.GetResults())
        {
            results.Add(c);
        }

        // SingleResponse wraps a single Create response with Open/Close,
        // so we should see: Open + Created (no Close)
        results.Count.ShouldBe(2);
        results[0].Action.ShouldBe(DaliLiveAction.Open);
        results[1].Action.ShouldBe(DaliLiveAction.Created);
    }

    [Test]
    public async Task GetResults_WhenNoEvents_ReturnsEmpty()
    {
        // A source that immediately completes without yielding any responses
        var source = EmptySource();
        var adapter = new SurrealDaliLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<DaliLiveChange<Person>>();
        await foreach (var c in adapter.GetResults())
        {
            results.Add(c);
        }

        results.ShouldBeEmpty();
    }

    private static async IAsyncEnumerable<SurrealDbLiveQueryResponse> EmptySource(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
