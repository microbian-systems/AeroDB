using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AeroDB.LiveQuery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.LiveQuery;
using NSubstitute;
using TUnit.Core;

namespace AeroDB.Tests;

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

    private static AeroDBLiveChange<Person> CreateOpen() =>
        new(AeroDBLiveAction.Open, null, null);

    private static AeroDBLiveChange<Person> CreateCreated(Person p) =>
        new(AeroDBLiveAction.Created, RecordKey(p), p);

    private static AeroDBLiveChange<Person> CreateUpdated(Person p) =>
        new(AeroDBLiveAction.Updated, RecordKey(p), p);

    private static AeroDBLiveChange<Person> CreateDeleted(Person p) =>
        new(AeroDBLiveAction.Deleted, RecordKey(p), p);

    private static AeroDBLiveChange<Person> CreateClosed(
        SurrealDbLiveQueryClosureReason reason = SurrealDbLiveQueryClosureReason.QueryKilled) =>
        new(AeroDBLiveAction.Closed, null, null, reason);

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
    public async Task SelectOnCreate_FiltersOnlyCreated()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectOnCreate());

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task SelectOnUpdate_FiltersOnlyUpdated()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectOnUpdate());

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task SelectOnDelete_FiltersOnlyDeleted()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };

        var source = new[]
        {
            CreateOpen(), CreateCreated(alice), CreateUpdated(bob),
            CreateDeleted(charlie), CreateClosed()
        }.ToObservable();

        var results = await ToListAsync(source.SelectOnDelete());

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
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
    }

    // ── Builder ToObservable Tests (need mocking) ───────────────

    [Test]
    public async Task ToObservable_Builder_ForwardsAllChanges()
    {
        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        var aliceOlder = new Person { Id = RecordId.From("person", "alice"), Name = "Alice (older)" };

        var query = Substitute.For<IAeroDBLiveQuery<Person>>();
        var builder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();

        builder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(query);

        var changes = new[] { CreateOpen(), CreateCreated(alice), CreateUpdated(aliceOlder), CreateClosed() };
        query.Changes(Arg.Any<CancellationToken>()).Returns(MakeAsyncEnumerable(changes));

        var observable = builder.ToObservable();
        var results = await ToListAsync(observable);

        results.Count.ShouldBe(4);
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Alice (older)");
        results[3].Action.ShouldBe(AeroDBLiveAction.Closed);
    }

    [Test]
    public async Task ToObservable_Builder_DisposesOnUnsubscribe()
    {
        var query = Substitute.For<IAeroDBLiveQuery<Person>>();
        var builder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();

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

        var query = Substitute.For<IAeroDBLiveQuery<Person>>();

        var changes = new[] { CreateCreated(alice), CreateUpdated(aliceOlder) };
        query.Changes(Arg.Any<CancellationToken>()).Returns(MakeAsyncEnumerable(changes));

        var observable = query.ToObservable();
        var results = await ToListAsync(observable);

        results.Count.ShouldBe(2);
        results[0].Action.ShouldBe(AeroDBLiveAction.Created);
        results[0].Document!.Name.ShouldBe("Alice");
        results[1].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[1].Document!.Name.ShouldBe("Alice (older)");
    }

    [Test]
    public async Task ToObservable_Query_DoesNotDisposeOnUnsubscribe()
    {
        var query = Substitute.For<IAeroDBLiveQuery<Person>>();

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

    private static async IAsyncEnumerable<AeroDBLiveChange<Person>> MakeAsyncEnumerable(
        AeroDBLiveChange<Person>[] changes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var c in changes)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
    }

    private static async IAsyncEnumerable<AeroDBLiveChange<Person>> MakeInfiniteEnumerable(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateOpen();

        // Block until cancellation is requested
        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }

    // ── SurrealDB LiveQuery Response Helpers ──────────────────────

    private static readonly ILogger<SurrealAeroDBLiveQuery<Person>> NullPersonLogger
        = NullLogger<SurrealAeroDBLiveQuery<Person>>.Instance;

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

    // ── IAeroDBLiveQuery<T> Filter Method Tests ────────────────────

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
        var adapter = new SurrealAeroDBLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<AeroDBLiveChange<Person>>();
        await foreach (var c in adapter.GetResults())
        {
            results.Add(c);
        }

        results.Count.ShouldBe(4); // Open + Created + Updated + Deleted (no Close)
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Alice Updated");
        results[3].Action.ShouldBe(AeroDBLiveAction.Deleted);
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
        var adapter = new SurrealAeroDBLiveQuery<Person>(
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
        var adapter = new SurrealAeroDBLiveQuery<Person>(
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
        var adapter = new SurrealAeroDBLiveQuery<Person>(
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
        var adapter = new SurrealAeroDBLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<AeroDBLiveChange<Person>>();
        await foreach (var c in adapter.GetResults())
        {
            results.Add(c);
        }

        // SingleResponse wraps a single Create response with Open/Close,
        // so we should see: Open + Created (no Close)
        results.Count.ShouldBe(2);
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
    }

    [Test]
    public async Task GetResults_WhenNoEvents_ReturnsEmpty()
    {
        // A source that immediately completes without yielding any responses
        var source = EmptySource();
        var adapter = new SurrealAeroDBLiveQuery<Person>(
            source, null, null, null, null, 4096, BoundedChannelFullMode.Wait, NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<AeroDBLiveChange<Person>>();
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

    // ── Shortcut Method Tests ─────────────────────────────────────

    /// <summary>Helper: converts an IEnumerable to IAsyncEnumerable for mock setup.</summary>
    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        IEnumerable<T> items,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var item in items) yield return item;
    }

    /// <summary>Collects items from an observable that completes normally.</summary>
    private static async Task<List<T>> CollectAsync<T>(IObservable<T> observable)
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

    [Test]
    public async Task OnCreate_ReturnsOnlyCreatedDocuments()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.OnCreate();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task OnUpdate_ReturnsOnlyUpdatedDocuments()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.OnUpdate();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task OnDelete_ReturnsOnlyDeletedDocuments()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.OnDelete();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task Results_ExcludesCloseEvents()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.Results();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(4); // Open + Created + Updated + Deleted, no Close
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Bob");
        results[3].Action.ShouldBe(AeroDBLiveAction.Deleted);
    }

    [Test]
    public async Task Results_WithOnlyOpenAndClose_EmitsOnlyOpen()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.Results();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
    }

    [Test]
    public async Task OnCreate_MultipleCreates_EmitsAllDocuments()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.OnCreate();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(3);
        results[0].Name.ShouldBe("Alice");
        results[1].Name.ShouldBe("Bob");
        results[2].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task OnCreate_WhenNoCreatedEvents_ReturnsEmpty()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        var obs = mockBuilder.OnCreate();
        var results = await CollectAsync(obs);

        results.ShouldBeEmpty();
    }

    [Test]
    public async Task OnUpdate_WhenSubscribeAsyncThrows_PropagatesError()
    {
        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IAeroDBLiveQuery<Person>>(
                new InvalidOperationException("Connection failed")));

        var obs = mockBuilder.OnUpdate();
        var tcs = new TaskCompletionSource<Exception>();
        obs.Subscribe(_ => { }, ex => tcs.TrySetResult(ex), () => { });

        var capturedEx = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capturedEx.ShouldBeOfType<InvalidOperationException>();
        capturedEx.Message.ShouldBe("Connection failed");
    }

    [Test]
    public async Task ToObservable_WhenSubscribeAsyncThrows_PropagatesError()
    {
        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IAeroDBLiveQuery<Person>>(
                new InvalidOperationException("Connection failed")));

        var obs = mockBuilder.ToObservable();
        var tcs = new TaskCompletionSource<Exception>();
        obs.Subscribe(_ => { }, ex => tcs.TrySetResult(ex), () => { });

        var capturedEx = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capturedEx.ShouldBeOfType<InvalidOperationException>();
        capturedEx.Message.ShouldBe("Connection failed");
    }

    // ── Channel-based Streaming Test ──────────────────────────────

    [Test]
    public async Task GetResults_WithChannelSource_ReceivesLiveUpdates()
    {
        var channel = Channel.CreateUnbounded<SurrealDbLiveQueryResponse>();

        var adapter = new SurrealAeroDBLiveQuery<Person>(
            channel.Reader.ReadAllAsync(),
            null, null, null, null,
            4096,
            BoundedChannelFullMode.Wait,
            NullPersonLogger);
        await adapter.StartAsync();

        var results = new List<AeroDBLiveChange<Person>>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var change in adapter.GetResults())
            {
                results.Add(change);
            }
        });

        // Push events into the channel
        await channel.Writer.WriteAsync(CreateSdkOpen());
        await Task.Delay(50);

        var alice = new Person { Id = RecordId.From("person", "alice"), Name = "Alice" };
        await channel.Writer.WriteAsync(CreateSdkCreate(alice));
        await Task.Delay(50);

        var bob = new Person { Id = RecordId.From("person", "bob"), Name = "Bob" };
        await channel.Writer.WriteAsync(CreateSdkUpdate(bob));
        await Task.Delay(50);

        var charlie = new Person { Id = RecordId.From("person", "charlie"), Name = "Charlie" };
        await channel.Writer.WriteAsync(CreateSdkDelete(charlie));
        await Task.Delay(50);

        await channel.Writer.WriteAsync(CreateSdkClose());
        await Task.Delay(50);

        // Complete the channel — this makes ReadAllAsync complete, which
        // causes the adapter's read loop to complete and GetResults() to end.
        channel.Writer.Complete();

        // Wait for the reader to finish processing all events
        await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify: 4 results (Open + Created + Updated + Deleted, no Close)
        results.Count.ShouldBe(4);
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Bob");
        results[3].Action.ShouldBe(AeroDBLiveAction.Deleted);
    }

    // ── Fluent Chain Tests ────────────────────────────────────────

    /// <summary>Shared mock builder with fluent Where configured.</summary>
    private static (IAeroDBLiveQueryBuilder<Person> builder, IAeroDBLiveQuery<Person> query) CreateFluentMock()
    {
        var changes = new[]
        {
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Open, null, null),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Created, "person:1", new Person { Name = "Alice" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Updated, "person:2", new Person { Name = "Bob" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Deleted, "person:3", new Person { Name = "Charlie" }),
            new AeroDBLiveChange<Person>(AeroDBLiveAction.Closed, null, null, SurrealDbLiveQueryClosureReason.QueryKilled),
        };

        var mockQuery = Substitute.For<IAeroDBLiveQuery<Person>>();
        mockQuery.Changes(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsyncEnumerable(changes, CancellationToken.None));
        mockQuery.DisposeAsync().Returns(ValueTask.CompletedTask);

        var mockBuilder = Substitute.For<IAeroDBLiveQueryBuilder<Person>>();
        mockBuilder.Where(Arg.Any<Expression<Func<Person, bool>>>()).Returns(mockBuilder);
        mockBuilder.SubscribeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(mockQuery));

        return (mockBuilder, mockQuery);
    }

    [Test]
    public async Task FluentChain_Where_ToObservable_SelectOnCreate_FlowsData()
    {
        var (mockBuilder, _) = CreateFluentMock();

        var results = new List<Person>();
        var tcs = new TaskCompletionSource<bool>();

        mockBuilder
            .Where(p => p.Age > 18)
            .ToObservable()
            .SelectOnCreate()
            .Subscribe(
                results.Add,
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult(true));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task FluentChain_OnCreate_Shortcut_FlowsData()
    {
        var (mockBuilder, _) = CreateFluentMock();

        var obs = mockBuilder.Where(p => p.Age > 18).OnCreate();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task FluentChain_OnUpdate_Shortcut_FlowsData()
    {
        var (mockBuilder, _) = CreateFluentMock();

        var obs = mockBuilder.Where(p => p.Age > 18).OnUpdate();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Bob");
    }

    [Test]
    public async Task FluentChain_OnDelete_Shortcut_FlowsData()
    {
        var (mockBuilder, _) = CreateFluentMock();

        var obs = mockBuilder.Where(p => p.Age > 18).OnDelete();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Charlie");
    }

    [Test]
    public async Task FluentChain_Results_Shortcut_ExcludesClose()
    {
        var (mockBuilder, _) = CreateFluentMock();

        var obs = mockBuilder.Where(p => p.Age > 18).Results();
        var results = await CollectAsync(obs);

        results.Count.ShouldBe(4); // Open + Created + Updated + Deleted
        results[0].Action.ShouldBe(AeroDBLiveAction.Open);
        results[1].Action.ShouldBe(AeroDBLiveAction.Created);
        results[1].Document!.Name.ShouldBe("Alice");
        results[2].Action.ShouldBe(AeroDBLiveAction.Updated);
        results[2].Document!.Name.ShouldBe("Bob");
        results[3].Action.ShouldBe(AeroDBLiveAction.Deleted);
    }

    [Test]
    public async Task FluentChain_Where_Results_AggregateRecords_FullChain()
    {
        var (mockBuilder, _) = CreateFluentMock();

        IDictionary<string, Person>? finalState = null;
        var tcs = new TaskCompletionSource<bool>();

        mockBuilder
            .Where(p => p.Age > 18)
            .Results()
            .AggregateRecords(new Dictionary<string, Person>())
            .Subscribe(
                state => { finalState = state; },
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult(true));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        finalState.ShouldNotBeNull();
        finalState.Count.ShouldBe(2); // Alice (Created) + Bob (Updated)
        finalState.ShouldContain(kvp => kvp.Value.Name == "Alice");
        finalState.ShouldContain(kvp => kvp.Value.Name == "Bob");
        finalState.ShouldNotContain(kvp => kvp.Value.Name == "Charlie");
    }
}
