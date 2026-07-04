using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AeroDB;
using AeroDB.LiveQuery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.LiveQuery;
using TUnit.Core;

namespace AeroDB.Tests;

// Test enum for H2 expression translation tests
public enum TestStatus { Draft = 0, Published = 1, Archived = 2 }

// Test model with enum property for H2
public class EnumTestDoc : Record
{
    public TestStatus Status { get; set; }
}

[NotInParallel]
public class LiveQueryTests
{
    private static readonly BoundedChannelFullMode Wait = BoundedChannelFullMode.Wait;
    private static readonly ILogger<SurrealDaliLiveQuery<Person>> NullPersonLogger
        = NullLogger<SurrealDaliLiveQuery<Person>>.Instance;

    private static LiveQuerySession CreateSession(out ISurrealDbSession sessionMock)
    {
        var session = Substitute.For<ISurrealDbSession>();
        sessionMock = session;
        return new LiveQuerySession(session, new StoreOptions(), NullLoggerFactory.Instance);
    }

    /// <summary>Helper: a fake source yielding a single response between Open/Close.</summary>
    private static async IAsyncEnumerable<SurrealDbLiveQueryResponse> SingleResponse(
        SurrealDbLiveQueryResponse response,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateOpen();
        yield return response;
        yield return CreateClose();
    }

    /// <summary>Helper: a fake source yielding multiple responses between Open/Close.</summary>
    private static async IAsyncEnumerable<SurrealDbLiveQueryResponse> MultipleResponses(
        SurrealDbLiveQueryResponse[] responses,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CreateOpen();
        foreach (var r in responses) yield return r;
        yield return CreateClose();
    }

    private static SurrealDbLiveQueryOpenResponse CreateOpen() =>
        (SurrealDbLiveQueryOpenResponse)Activator.CreateInstance(typeof(SurrealDbLiveQueryOpenResponse), nonPublic: true)!;
    private static SurrealDbLiveQueryCloseResponse CreateClose() =>
        (SurrealDbLiveQueryCloseResponse)Activator.CreateInstance(typeof(SurrealDbLiveQueryCloseResponse),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { SurrealDbLiveQueryClosureReason.QueryKilled }, null)!;
    private static SurrealDbLiveQueryCreateResponse<T> CreateCreate<T>(T r) =>
        (SurrealDbLiveQueryCreateResponse<T>)Activator.CreateInstance(typeof(SurrealDbLiveQueryCreateResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { r! }, null)!;
    private static SurrealDbLiveQueryUpdateResponse<T> CreateUpdate<T>(T r) =>
        (SurrealDbLiveQueryUpdateResponse<T>)Activator.CreateInstance(typeof(SurrealDbLiveQueryUpdateResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { r! }, null)!;
    private static SurrealDbLiveQueryDeleteResponse<T> CreateDelete<T>(T r) =>
        (SurrealDbLiveQueryDeleteResponse<T>)Activator.CreateInstance(typeof(SurrealDbLiveQueryDeleteResponse<T>),
            BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { r! }, null)!;

    // ─────────────────────────────────────────────────────────────
    //  BEHAVIORAL TESTS
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Changes_receives_created_event()
    {
        var alice = new Person { Name = "Alice", Age = 30 };
        var source = SingleResponse(CreateCreate(alice));
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        var changes = new List<DaliLiveChange<Person>>();
        await foreach (var c in adapter.Changes()) { changes.Add(c); if (c.Action == DaliLiveAction.Closed) break; }

        changes.Count.ShouldBe(3);
        changes[0].Action.ShouldBe(DaliLiveAction.Open);
        changes[1].Action.ShouldBe(DaliLiveAction.Created);
        changes[1].Document!.Name.ShouldBe("Alice");
        changes[2].Action.ShouldBe(DaliLiveAction.Closed);
    }

    [Test]
    public async Task Changes_receives_updated_event()
    {
        var bob = new Person { Name = "Bob", Age = 25 };
        var source = SingleResponse(CreateUpdate(bob));
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        var actions = new List<DaliLiveAction>();
        await foreach (var c in adapter.Changes()) { actions.Add(c.Action); if (c.Action == DaliLiveAction.Closed) break; }

        actions.ShouldContain(DaliLiveAction.Updated);
        actions.ShouldNotContain(DaliLiveAction.Created);
    }

    [Test]
    public async Task Changes_receives_deleted_event()
    {
        var charlie = new Person { Name = "Charlie", Age = 40 };
        var source = SingleResponse(CreateDelete(charlie));
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        var actions = new List<DaliLiveAction>();
        await foreach (var c in adapter.Changes()) { actions.Add(c.Action); if (c.Action == DaliLiveAction.Closed) break; }

        actions.ShouldContain(DaliLiveAction.Deleted);
    }

    [Test]
    public async Task Changes_receives_multiple_events_in_order()
    {
        var dave = new Person { Name = "Dave", Age = 20 };
        var eve = new Person { Name = "Eve", Age = 22 };
        var frank = new Person { Name = "Frank", Age = 35 };
        var source = MultipleResponses(new SurrealDbLiveQueryResponse[] {
            CreateCreate(dave), CreateUpdate(eve), CreateDelete(frank) });
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        var actions = new List<DaliLiveAction>();
        await foreach (var c in adapter.Changes()) { actions.Add(c.Action); if (c.Action == DaliLiveAction.Closed) break; }

        actions.Count.ShouldBe(5);
        actions[0].ShouldBe(DaliLiveAction.Open);
        actions[1].ShouldBe(DaliLiveAction.Created);
        actions[2].ShouldBe(DaliLiveAction.Updated);
        actions[3].ShouldBe(DaliLiveAction.Deleted);
        actions[4].ShouldBe(DaliLiveAction.Closed);
    }

    [Test]
    public async Task Changes_closure_reason_is_populated()
    {
        var source = SingleResponse(CreateCreate(new Person { Name = "X", Age = 1 }));
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        DaliLiveChange<Person>? closed = null;
        await foreach (var c in adapter.Changes()) { if (c.Action == DaliLiveAction.Closed) { closed = c; break; } }

        closed.ShouldNotBeNull();
        closed!.ClosureReason.ShouldBe(SurrealDbLiveQueryClosureReason.QueryKilled);
    }

    // ── Callbacks ────────────────────────────────────────────────

    [Test]
    public async Task OnOpen_fires_when_subscription_opens()
    {
        var alice = new Person { Name = "Alice", Age = 30 };
        var source = SingleResponse(CreateCreate(alice));
        bool opened = false;
        var tcs = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, new Action[] { () => { opened = true; tcs.TrySetResult(); } }, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();

        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        opened.ShouldBeTrue("OnOpen should fire when subscription opens");
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task OnCreated_callback_fires()
    {
        var alice = new Person { Name = "Alice", Age = 30 };
        var source = SingleResponse(CreateCreate(alice));
        Person? received = null; var tcs = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source, new Action<Person>[] { p => { received = p; tcs.TrySetResult(); } }, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        received.ShouldNotBeNull(); received!.Name.ShouldBe("Alice");
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task OnUpdated_callback_fires()
    {
        var bob = new Person { Name = "Bob", Age = 25 };
        var source = SingleResponse(CreateUpdate(bob));
        Person? received = null; var tcs = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, new Action<Person>[] { p => { received = p; tcs.TrySetResult(); } }, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        received.ShouldNotBeNull(); received!.Name.ShouldBe("Bob");
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task OnDeleted_callback_fires()
    {
        var charlie = new Person { Name = "Charlie", Age = 40 };
        var source = SingleResponse(CreateDelete(charlie));
        Person? received = null; var tcs = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, new Action<Person>[] { p => { received = p; tcs.TrySetResult(); } }, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        received.ShouldNotBeNull(); received!.Name.ShouldBe("Charlie");
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task OnCreated_does_not_fire_for_update_or_delete()
    {
        var person = new Person { Name = "X", Age = 1 };
        var source = MultipleResponses(new SurrealDbLiveQueryResponse[] { CreateUpdate(person), CreateDelete(person) });
        int createdFired = 0, eventCount = 0; var allDone = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source,
            new Action<Person>[] { _ => Interlocked.Increment(ref createdFired) },
            new Action<Person>[] { _ => { if (Interlocked.Increment(ref eventCount) == 2) allDone.TrySetResult(); } },
            new Action<Person>[] { _ => { if (Interlocked.Increment(ref eventCount) == 2) allDone.TrySetResult(); } },
            null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await Task.WhenAny(allDone.Task, Task.Delay(5000));
        createdFired.ShouldBe(0);
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task one_bad_callback_does_not_kill_other_callbacks()
    {
        var p1 = new Person { Name = "Good1", Age = 1 };
        var p2 = new Person { Name = "Good2", Age = 2 };
        var source = MultipleResponses(new SurrealDbLiveQueryResponse[] { CreateCreate(p1), CreateCreate(p2) });
        Person? received = null; var tcs = new TaskCompletionSource();
        var adapter = new SurrealDaliLiveQuery<Person>(source,
            new Action<Person>[] { p => throw new InvalidOperationException("Intentional failure"), p => { received = p; tcs.TrySetResult(); } },
            null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        received.ShouldNotBeNull(); received!.Name.ShouldBe("Good1");
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task Changes_throws_when_callbacks_are_active()
    {
        var source = SingleResponse(CreateCreate(new Person { Name = "X", Age = 1 }));
        var adapter = new SurrealDaliLiveQuery<Person>(source, new Action<Person>[] { _ => { } }, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        Should.Throw<InvalidOperationException>(() => adapter.Changes());
        await adapter.DisposeAsync();
    }

    [Test]
    public async Task StopAsync_stops_the_stream_and_is_idempotent()
    {
        var source = SingleResponse(CreateCreate(new Person { Name = "X", Age = 1 }));
        var adapter = new SurrealDaliLiveQuery<Person>(source, null, null, null, null, 4096, Wait, NullPersonLogger);
        await adapter.StartAsync();
        await adapter.StopAsync();
        await adapter.StopAsync(); // idempotent
    }

    // ─────────────────────────────────────────────────────────────
    //  FLUENT BUILDER TESTS
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Live_Where_AgeGt18_constructs_correct_live_sql()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => x.Age > 18).SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("LIVE SELECT * FROM `person`") && s.Contains("Age > 18")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Live_no_where_uses_live_table_not_raw_query()
    {
        var qs = CreateSession(out var session);
        session.LiveTable<Person>(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().SubscribeAsync();
        await session.Received(1).LiveTable<Person>("person", false, Arg.Any<CancellationToken>());
        await session.DidNotReceive().LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Live_multiple_where_clauses_are_and_combined()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => x.Age > 18).Where(x => x.Name != null).SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("Age > 18") && s.Contains("AND") && s.Contains("Name")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Live_Where_with_equals_constructs_correct_sql()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => x.Name == "Alice").SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("WHERE") && s.Contains("Name")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LiveRawQuery_passes_surql_and_parameters_to_session()
    {
        var qs = CreateSession(out var session);
        var parameters = new Dictionary<string, object?> { ["min"] = 18 };
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.LiveRawQuery<Person>("LIVE SELECT * FROM person WHERE age > $min", parameters);
        await session.Received(1).LiveRawQuery<Person>("LIVE SELECT * FROM person WHERE age > $min", parameters, Arg.Any<CancellationToken>());
    }

    // ─────────────────────────────────────────────────────────────
    //  EXPRESSION TRANSLATOR FIXES (H1, H2, H3)
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task TranslateCondition_null_equality_emits_NONE()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => x.Name == null).SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("Name") && s.Contains("NONE")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TranslateCondition_not_null_emits_NONE()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => x.Name != null).SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("Name") && s.Contains("NONE") && s.Contains("!=")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TranslateCondition_enum_equality_emits_quoted_integer()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<EnumTestDoc>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<EnumTestDoc>>(default!));
        await using var sub = await qs.Live<EnumTestDoc>().Where(x => x.Status == TestStatus.Published).SubscribeAsync();
        await session.Received(1).LiveRawQuery<EnumTestDoc>(Arg.Is<string>(s => !s.Contains("= Published") && s.Contains("Status")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TranslateCondition_list_contains_emits_inside()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        var ids = new List<int> { 1, 2, 3 };
        await using var sub = await qs.Live<Person>().Where(x => ids.Contains(x.Age)).SubscribeAsync();
        await session.Received(1).LiveRawQuery<Person>(Arg.Is<string>(s => s.Contains("INSIDE") && s.Contains("[1, 2, 3]")), null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TranslateCondition_empty_list_skips_inside()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var sub = await qs.Live<Person>().Where(x => new List<int>().Contains(x.Age)).SubscribeAsync();
    }

    // ─────────────────────────────────────────────────────────────
    //  CONTRACT TESTS
    // ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Live_returns_IDaliLiveQuery()
    {
        var qs = CreateSession(out var session);
        session.LiveTable<Person>(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        var result = await qs.Live<Person>().SubscribeAsync();
        result.ShouldNotBeNull();
        result.ShouldBeAssignableTo<IDaliLiveQuery<Person>>();
    }

    [Test]
    public async Task LiveRawQuery_returns_IDaliLiveQuery()
    {
        var qs = CreateSession(out var session);
        session.LiveRawQuery<Person>(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        var result = await qs.LiveRawQuery<Person>("LIVE SELECT * FROM person");
        result.ShouldNotBeNull();
        result.ShouldBeAssignableTo<IDaliLiveQuery<Person>>();
    }

    [Test]
    public void ILiveQuerySession_extends_IAsyncDisposable()
    {
        ILiveQuerySession session = CreateSession(out _);
        IAsyncDisposable disposable = session;
        _ = disposable;
    }

    [Test]
    public void IDaliLiveQuery_extends_IAsyncDisposable()
    {
        IDaliLiveQuery<Person> query = null!;
        IAsyncDisposable disposable = query;
        _ = disposable;
    }

    [Test]
    public async Task IDaliLiveQuery_has_Reader_property()
    {
        var qs = CreateSession(out var session);
        session.LiveTable<Person>(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SurrealDbLiveQuery<Person>>(default!));
        await using var query = await qs.Live<Person>().SubscribeAsync();
        var reader = query.Reader;
        _ = reader;
    }
}
