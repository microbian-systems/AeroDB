# Dali Live Query API — Implementation Plan

## Status: Design Phase — Council Reviewed (22 items, 6 Critical + 6 High + 10 Medium/Low)

---

## 1. Motivation

Current live query API (`WatchTableAsync`, `WatchQueryAsync`, `WatchStreamAsync`) is a thin wrapper
around the SurrealDB SDK with minimal Dali value-add:

- No fluent/typed API — raw SurrealQL strings only
- Methods live on `IQuerySession`/`IDocumentSession`, violating SRP (query session does queries
  AND live subscriptions)
- No Dali-level live-change abstraction — consumers deal with SDK types
  (`SurrealDbLiveQuery<T>`, raw response types)
- No expression-based `Where()` filtering — users write SurrealQL by hand

Goal: a provider-neutral, fluent, SRP-compliant live query subsystem.

---

## 2. Proposed API Surface

### 2.1 Store Entry Point (SRP)

```csharp
// New method on IDocumentStore:
Task<ILiveQuerySession> LiveQuerySessionAsync(CancellationToken ct = default);
```

`ILiveQuerySession` is a **new session type** dedicated to live queries. It is NOT
`IQuerySession` — query sessions are for point-in-time reads; live sessions are for
ongoing subscriptions. Backward compat: existing `Watch*` methods remain on
`IQuerySession`/`IDocumentSession` (deprecated but not removed).

### 2.2 Fluent Builder API

```csharp
public interface ILiveQuerySession : IAsyncDisposable
{
    IDaliLiveQueryBuilder<T> Live<T>() where T : class;
}

public interface IDaliLiveQueryBuilder<T> where T : class
{
    /// <summary>
    /// Adds a WHERE condition. Multiple calls are ANDed.
    /// Expression is translated to SurrealQL via the existing <see cref="SurrealExpressionVisitor"/>.
    /// </summary>
    IDaliLiveQueryBuilder<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Select specific fields to return (SurrealQL SELECT projection).
    /// If not called, SELECT * is used.
    /// </summary>
    IDaliLiveQueryBuilder<T> Select(params Expression<Func<T, object>>[] fields);

    /// <summary>
    /// Register a callback for CREATE events.
    /// </summary>
    IDaliLiveQueryBuilder<T> OnCreated(Action<T> handler);

    /// <summary>
    /// Register a callback for UPDATE events.
    /// </summary>
    IDaliLiveQueryBuilder<T> OnUpdated(Action<T> handler);

    /// <summary>
    /// Register a callback for DELETE events. Receives the last-known document.
    /// </summary>
    IDaliLiveQueryBuilder<T> OnDeleted(Action<T> handler);

    /// <summary>
    /// Start the live query and return a disposable subscription handle.
    /// </summary>
    Task<IDaliLiveQuery<T>> SubscribeAsync(CancellationToken ct = default);
}
```

### 2.3 Change Abstraction

```csharp
public enum DaliLiveAction
{
    Open,       // Subscription established
    Created,    // Record inserted
    Updated,    // Record modified
    Deleted,    // Record removed
    Closed      // Subscription ended (socket close, kill, etc.)
}

public sealed record DaliLiveChange<T>(
    DaliLiveAction Action,
    string? Id,        // SurrealDB record ID (table:id format)
    T? Document        // null for Open/Close, may be null for Deleted
);

public interface IDaliLiveQuery<T> : IAsyncDisposable
{
    /// <summary>
    /// Streams all live change events (Open → CUD → Close).
    /// </summary>
    IAsyncEnumerable<DaliLiveChange<T>> Changes(CancellationToken ct = default);

    /// <summary>
    /// Direct access to the underlying channel for custom pipelines.
    /// Returns null when callbacks (OnCreated etc.) are actively consuming the channel.
    /// </summary>
    ChannelReader<DaliLiveChange<T>>? Reader { get; }

    /// <summary>
    /// Kills the server-side live query. Equivalent to DisposeAsync.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
```

### 2.4 Usage Examples

**Fluent with callbacks:**

```csharp
await using var session = await store.LiveQuerySessionAsync();

await session.Live<Person>()
    .Where(x => x.Age >= 18)
    .OnCreated(p => Console.WriteLine($"New adult: {p.Name}"))
    .OnUpdated(p => Console.WriteLine($"Adult changed: {p.Name}"))
    .OnDeleted(p => Console.WriteLine($"Adult removed: {p.Name}"))
    .SubscribeAsync(ct);
```

**Fluent with `await foreach`:**

```csharp
await using var live = await session.Live<Person>()
    .Where(x => x.Age >= 18)
    .SubscribeAsync(ct);

await foreach (var change in live.Changes(ct))
{
    switch (change.Action)
    {
        case DaliLiveAction.Created:
            await bus.Publish(new MemberCreated(change.Document!));
            break;
        case DaliLiveAction.Updated:
            await cache.SetAsync(change.Id!, change.Document!);
            break;
        case DaliLiveAction.Deleted:
            await cache.RemoveAsync(change.Id!);
            break;
    }
}
```

**Raw SurrealQL fallback (when expression translation isn't sufficient):**

```csharp
// Retained for advanced use cases —
// expose on ILiveQuerySession for SRP compliance:
await using var live = await session.LiveRawQuery<Person>(
    "LIVE SELECT name, age FROM person WHERE age > $min",
    new Dictionary<string, object?> { ["min"] = 18 },
    ct);
```

---

## 3. SurrealDB Provider Implementation

### 3.1 Expression Translation

Reuse the existing `SurrealExpressionVisitor.TranslateCondition(Expression)` static method
(from `src/Dali/Linq/ExpressionVisitor.cs:260-268`). It already handles:

- Binary expressions (`==`, `!=`, `>`, `<`, `>=`, `<=`, `&&`, `||`)
- Method calls (`.Contains()`, `.StartsWith()`, `.EndsWith()`, `.Any()`)
- Member access (property chains)
- Constants/parameters

The fluent `.Where(x => x.Age >= 18)` produces a `BinaryExpression` that
`TranslateCondition` can convert to `age >= 18`.

### 3.2 SQL Construction

```
.Live<Person>()                                      → "LIVE SELECT * FROM `person`"
  .Where(x => x.Age >= 18)                           → " WHERE age >= 18"
  .Select(x => new { x.Name, x.Age })                → "LIVE SELECT name, age FROM `person`"
  .Where(x => x.Age >= 18)
```

The `BuildSurrealQL()` method constructs:

```
LIVE SELECT {fields} FROM `{table}` WHERE {conditions}
```

Table name resolved via `MetadataDispatch.GetTableName(typeof(T))`.

### 3.3 Subscription Wiring (CRITICAL fixes applied)

**C1 fixed:** `Task.Run` moved from constructor to `StartAsync()`. Constructor only stores config.
**C2 fixed:** `SurrealDbLiveQuery<T>` already implements `IAsyncEnumerable` — passed directly, no bridge needed.
**C5 fixed:** `WatchQueryAsync(string)` routes through `LiveRawQuery`, not the expression builder.

```csharp
internal sealed class SurrealLiveQueryBuilder<T> : IDaliLiveQueryBuilder<T> where T : class
{
    private readonly ISurrealDbSession _session;
    private readonly StoreOptions _options;
    private readonly ILogger<SurrealLiveQueryBuilder<T>> _logger;
    private readonly List<Expression<Func<T, bool>>> _predicates = new();
    private readonly List<Expression<Func<T, object>>> _fields = new();
    private readonly List<Action<T>> _onCreated = new();
    private readonly List<Action<T>> _onUpdated = new();
    private readonly List<Action<T>> _onDeleted = new();

    public IDaliLiveQueryBuilder<T> Where(Expression<Func<T, bool>> predicate) { ...; return this; }
    public IDaliLiveQueryBuilder<T> Select(params Expression<Func<T, object>>[] fields) { ...; return this; }
    public IDaliLiveQueryBuilder<T> OnCreated(Action<T> h) { _onCreated.Add(h); return this; }
    public IDaliLiveQueryBuilder<T> OnUpdated(Action<T> h) { _onUpdated.Add(h); return this; }
    public IDaliLiveQueryBuilder<T> OnDeleted(Action<T> h) { _onDeleted.Add(h); return this; }

    public async Task<IDaliLiveQuery<T>> SubscribeAsync(CancellationToken ct = default)
    {
        var table = MetadataDispatch.GetTableName(typeof(T));

        // C2 FIXED: SurrealDbLiveQuery<T> IS IAsyncEnumerable — use directly
        SurrealDbLiveQuery<T> sdkLive;
        if (_predicates.Count == 0 && _fields.Count == 0)
        {
            sdkLive = await _session.LiveTable<T>(table, diff: false, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var sql = BuildSurrealQL(table);
            sdkLive = await _session.LiveRawQuery<T>(sql, null, ct)
                .ConfigureAwait(false);
        }

        // C1 FIXED: constructor only stores config; StartAsync fires the loops
        var query = new SurrealDaliLiveQuery<T>(
            sdkLive,
            _onCreated, _onUpdated, _onDeleted,
            _options.LiveQueryChannelCapacity,
            _options.LiveQueryChannelFullMode,
            _logger);
        await query.StartAsync(ct).ConfigureAwait(false);
        return query;
    }

    private string BuildSurrealQL(string table)
    {
        var fields = _fields.Count > 0
            ? string.Join(", ", _fields.Select(TranslateField))
            : "*";
        var conditions = _predicates.Count > 0
            ? " WHERE " + string.Join(" AND ", _predicates
                .Select(p => SurrealExpressionVisitor.TranslateCondition(p.Body)))
            : "";
        return $"LIVE SELECT {fields} FROM `{table}`{conditions}";
    }
}
```

### 3.4 Bounded Channel Adapter (Architect-Approved + CRITICAL fixes)

**C1 fixed:** Constructor stores config only. `StartAsync(ct)` fires `_readLoop` + optional `_callbackLoop`.
**C3 fixed:** Each callback wrapped in try/catch — one bad handler doesn't kill the dispatch loop.
**C6 fixed:** `StopAsync` uses `TryComplete()` for idempotent completion.

```csharp
internal sealed class SurrealDaliLiveQuery<T> : IDaliLiveQuery<T> where T : class
{
    private readonly Channel<DaliLiveChange<T>> _channel;
    private readonly SurrealDbLiveQuery<T> _sdkLive;
    private readonly IReadOnlyList<Action<T>>? _onCreated;
    private readonly IReadOnlyList<Action<T>>? _onUpdated;
    private readonly IReadOnlyList<Action<T>>? _onDeleted;
    private readonly ILogger<SurrealDaliLiveQuery<T>> _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _readLoop;
    private Task? _callbackLoop;
    private int _changesConsumed; // 0 = available, 1 = consumed, see H4

    // C1 FIXED: constructor only stores config — no Task.Run
    internal SurrealDaliLiveQuery(
        SurrealDbLiveQuery<T> sdkLive,               // C2: direct type, not IAsyncEnumerable
        IReadOnlyList<Action<T>>? onCreated,
        IReadOnlyList<Action<T>>? onUpdated,
        IReadOnlyList<Action<T>>? onDeleted,
        int channelCapacity,
        BoundedChannelFullMode fullMode,
        ILogger<SurrealDaliLiveQuery<T>> logger)
    {
        _sdkLive = sdkLive;
        _onCreated = onCreated;
        _onUpdated = onUpdated;
        _onDeleted = onDeleted;
        _logger = logger;

        _channel = Channel.CreateBounded<DaliLiveChange<T>>(
            new BoundedChannelOptions(channelCapacity)
            {
                FullMode = fullMode,
                SingleWriter = true,
                SingleReader = false
            });
    }

    // C1 FIXED: explicit start — caller observes Open event after this returns
    public async Task StartAsync(CancellationToken ct = default)
    {
        _readLoop = Task.Run(() => ReadFromSdkAsync(ct));
        _logger.LogDebug("Live query read loop started for {Type}", typeof(T).Name);

        bool hasCallbacks = (_onCreated is { Count: > 0 })
                         || (_onUpdated is { Count: > 0 })
                         || (_onDeleted is { Count: > 0 });

        if (hasCallbacks)
        {
            _callbackLoop = Task.Run(() => DispatchCallbacksAsync());
            _logger.LogDebug("Live query callback loop started for {Type}", typeof(T).Name);
        }
    }

    private async Task ReadFromSdkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var response in _sdkLive.WithCancellation(ct).ConfigureAwait(false))
            {
                var change = MapToDaliChange(response);
                await _channel.Writer.WriteAsync(change, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live query read loop failed for {Type}", typeof(T).Name);
        }
        finally
        {
            _channel.Writer.TryComplete();
            _logger.LogDebug("Live query read loop completed for {Type}", typeof(T).Name);
        }
    }

    // C3 FIXED: each callback wrapped in try/catch
    private async Task DispatchCallbacksAsync()
    {
        await foreach (var change in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            switch (change.Action)
            {
                case DaliLiveAction.Created when change.Document is not null:
                    if (_onCreated != null)
                        foreach (var h in _onCreated)
                            InvokeSafe(h, change.Document, "OnCreated");
                    break;
                case DaliLiveAction.Updated when change.Document is not null:
                    if (_onUpdated != null)
                        foreach (var h in _onUpdated)
                            InvokeSafe(h, change.Document, "OnUpdated");
                    break;
                case DaliLiveAction.Deleted when change.Document is not null:
                    if (_onDeleted != null)
                        foreach (var h in _onDeleted)
                            InvokeSafe(h, change.Document, "OnDeleted");
                    break;
            }
        }
    }

    private void InvokeSafe(Action<T> handler, T document, string callbackName)
    {
        try { handler(document); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Callback} handler threw for document {Id}", callbackName, ExtractId(document));
        }
    }

    // ── IDaliLiveQuery<T> ──────────────────────────────────────

    public IAsyncEnumerable<DaliLiveChange<T>> Changes(CancellationToken ct = default)
    {
        // H4: throw if callbacks are consuming the channel
        if (_callbackLoop != null)
            throw new InvalidOperationException(
                "Cannot call Changes() when callbacks are active. Use one consumption model per subscription.");

        if (Interlocked.Exchange(ref _changesConsumed, 1) == 1)
            throw new InvalidOperationException(
                "Changes() has already been enumerated. Create a new subscription.");

        return _channel.Reader.ReadAllAsync(ct);
    }

    public ChannelReader<DaliLiveChange<T>>? Reader
        => _callbackLoop is null ? _channel.Reader : null;

    // C6 FIXED: TryComplete for idempotency
    public async Task StopAsync(CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        if (_readLoop != null) await _readLoop.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        _sdkLive.DisposeAsync(); // kills server-side query
        try { if (_readLoop != null) await _readLoop; } catch { }
        try { if (_callbackLoop != null) await _callbackLoop; } catch { }
        _cts.Dispose();
    }

    private static DaliLiveChange<T> MapToDaliChange(SurrealDbLiveQueryResponse response)
        => response switch
        {
            SurrealDbLiveQueryOpenResponse =>
                new(DaliLiveAction.Open, null, default),
            SurrealDbLiveQueryCreateResponse<T> c =>
                new(DaliLiveAction.Created, ExtractId(c.Result), c.Result),
            SurrealDbLiveQueryUpdateResponse<T> u =>
                new(DaliLiveAction.Updated, ExtractId(u.Result), u.Result),
            SurrealDbLiveQueryDeleteResponse<T> d =>
                new(DaliLiveAction.Deleted, ExtractId(d.Result), d.Result),
            SurrealDbLiveQueryCloseResponse close =>
                new(DaliLiveAction.Closed, null, default),
            _ => throw new NotSupportedException($"Unknown response: {response.GetType()}")
        };

    private static string? ExtractId(T? doc)
    {
        if (doc is Record r) return r.Id?.ToString();
        return doc?.GetType().GetProperty("Id")?.GetValue(doc)?.ToString();
    }
}
```

---

## 4. File Layout

### New files (src/Dali/LiveQuery/)

```
LiveQuery/
├── DaliLiveAction.cs              — enum
├── DaliLiveChange.cs              — record
├── IDaliLiveQuery.cs              — IDaliLiveQuery<T> interface
├── IDaliLiveQueryBuilder.cs       — IDaliLiveQueryBuilder<T> interface
├── ILiveQuerySession.cs           — ILiveQuerySession interface
├── LiveQuerySession.cs            — implementation
├── SurrealLiveQueryBuilder.cs     — builder implementation
└── SurrealDaliLiveQuery.cs        — bounded channel adapter
```

### Modified files

- `src/Dali/IDocumentStore.cs` — add `LiveQuerySessionAsync()`; remove `Watch*` from `IDocumentSession`
- `src/Dali/DocumentStore.cs` — implement `LiveQuerySessionAsync()`; remove `Watch*` impls
- `src/Dali/IQuerySession.cs` — remove `WatchTableAsync`, `WatchQueryAsync`, `WatchStreamAsync`
- `src/Dali/QuerySession.cs` — remove `Watch*` implementations
- `src/Dali/DocumentSession.cs` — remove `Watch*` implementations
- `src/Dali/StoreOptions.cs` — add `LiveQueryChannelCapacity` (4096), `LiveQueryChannelFullMode` (Wait)

### Deleted files

- `src/Dali/Projections/ILiveQuery.cs` — replaced by `IDaliLiveQuery<T>`
- `src/Dali/Projections/LiveQuery.cs` — replaced by `SurrealDaliLiveQuery<T>`

---

## 5. Design Decisions (Resolved)

### D1: Session Lifetime Model → Separate `ILiveQuerySession`

`IDocumentStore.LiveQuerySessionAsync()` returns a dedicated `ILiveQuerySession : IAsyncDisposable`.
Dispose kills all active subscriptions. Keeps read queries and live subscriptions in separate
types — both exist on `IDocumentStore` but serve different purposes.

### D2: Callback Threading Model → Channel&lt;T&gt; backbone (no DispatchMode enum)

**Architect decision:** Bounded `Channel<T>` as the single backbone. One mechanism, two
consumption paths. No `DispatchMode` enum — pick the correct default and expose a
`ChannelReader` escape hatch for power users.

**Why:**
- SurrealDB SDK already uses `Channel.CreateUnbounded()` internally — Dali adds a
  bounded channel (capacity 1024, `Wait` mode) as the backpressure boundary
- Inline dispatch blocks the SDK reader → footgun
- `Task.Run` per event → unbounded concurrency, GC churn
- `DispatchMode` enum → YAGNI; users pick the wrong one

**Architecture:**

```
SDK WebSocket ──▶ SDK unbounded Channel ──▶ Dali read loop (1 Task)
                                               │
                                               ▼
                                          Dali Bounded Channel (1024, Wait)
                                               │
                                    ┌──────────┴──────────┐
                                    ▼                      ▼
                            ChannelReader          Background callback Task
                            ── Changes(ct)         ── OnCreated/Updated/Deleted
                            (pull model)           (sequential push model)
```

**Properties:**
- Bounded channel provides backpressure — writer blocks when full
- Sequential callback dispatch (single Task, no locks) — slow handlers delay subsequent handlers (correct signal: writer backpressures)
- `Changes(ct)` = `_channel.Reader.ReadAllAsync(ct)` — one-liner
- `ChannelReader<T>? Reader` property for advanced users (fan-out, custom pipelines)
- Callbacks and `Changes(ct)` are mutually exclusive — user picks one per subscription

**Configuration** (on `StoreOptions`):

```csharp
/// <summary>
/// Max items buffered in the live query channel before backpressure is applied.
/// Default: 4096.
/// </summary>
public int LiveQueryChannelCapacity { get; set; } = 4096;

/// <summary>
/// Behavior when the live query channel is full.
/// Default: Wait (block writer until space is available).
/// </summary>
public BoundedChannelFullMode LiveQueryChannelFullMode { get; set; } = BoundedChannelFullMode.Wait;
```

The builder reads `StoreOptions` from the session and passes capacity/mode to `SurrealDaliLiveQuery<T>`'s constructor.

### D3: Backward Compatibility → Clean break (no adapters)

Old `WatchTableAsync` / `WatchQueryAsync` / `WatchStreamAsync` removed from
`IQuerySession` and `IDocumentSession`. Old `ILiveQuery<T>` interface and `LiveQuery<T>`
class deleted. New `IDaliLiveQuery<T>` is the only live query API. No adapters, no
deprecation period — clean slate.

### D4: Raw SurrealQL Escape Hatch → Yes

`ILiveQuerySession` exposes `LiveRawQuery<T>(string surql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)` for cases the expression translator cannot express (joins, subqueries, SurrealQL-specific features).

### D5: Stream Watching → Separate concern (Wolverine)

SurrealDB live queries and Wolverine event subscriptions are distinct subsystems.
`WatchStreamAsync` watches SurrealDB's internal `mt_events` table, but Dali's event
sourcing uses Wolverine for event delivery. These remain separate — the fluent live
query API focuses on document tables, not event streams.

### D6: Store Registration → Fallback-compatible

`MetadataDispatch.GetTableName(typeof(T))` already falls back to `ToSnakeCase(type.Name)`
for unregistered types. No explicit registration required — the fluent API works with
any POCO.

### D7: Multiple Where Clauses → AND only

Multiple `.Where()` calls are ANDed. OR logic is expressed within a single lambda:
`.Where(x => x.Age < 18 || x.Age > 65)`. No `.OrWhere()` needed — the expression
tree already captures the operator.

---

## 6. Architect Review Needed (Q2 — Threading Model)

The callback threading model needs architect review. Dispatch to @oracle with the
following brief:

> Dali's live query API has two consumption patterns:
> 1. `await foreach (var change in live.Changes(ct))` — IAsyncEnumerable pull model
> 2. `.OnCreated(h).OnUpdated(h).SubscribeAsync(ct)` — callback push model
>
> For pattern 2, callbacks can fire: inline on the enumerator thread, queued to ThreadPool,
> or via a `Channel<T>`. We could also expose a `Channel<T>` directly for producer/consumer
> decoupling. Is `yield return` + `IAsyncEnumerable` sufficient, or should we add
> `Channel<T>` as a first-class output? Should DispatchMode be configurable per-subscription?

---

## 8. Council Review — Action Items

**Verdict:** Architecture is correct. 22 concrete issues to fix before/after implementation.

### CRITICAL (fixed in design)

| # | Issue | Fix | Status |
|---|---|---|---|
| C1 | `Task.Run` in constructor → dead subscription | `StartAsync(ct)` added | ✅ §3.4 |
| C2 | `GetAsyncEnumerator` → `IAsyncEnumerable` bridge broken | `SurrealDbLiveQuery<T>` passed directly | ✅ §3.3 |
| C3 | No try/catch around callback invocations | `InvokeSafe()` wrapper per callback | ✅ §3.4 |
| C4 | `LegacyLiveQueryAdapter<T>` needed | **No longer needed** — clean break, no adapters | ✅ D3 |
| C5 | `WatchQueryAsync(string)` incompatible | **No longer exists** — old API deleted | ✅ D3 |
| C6 | `StopAsync` uses `Complete()` → throws if already completed | `TryComplete()` for idempotency | ✅ §3.4 |

### HIGH (fix before merging)

| # | Issue | Fix |
|---|---|---|
| H1 | Null checks (`x.Name != null`) translate to `Name != null` — SurrealDB needs `IS NOT NONE` | Add null-check pattern to `TranslateCondition` |
| H2 | Enum equality emits unquoted string → treated as field reference | Emit enum as int or quoted string |
| H3 | `List<T>.Contains(x.Id)` emits garbage — needs `INSIDE [...]` | Add `List.Contains` → `INSIDE` translation |
| H4 | `Changes()` silently returns empty when callbacks are active | Throw `InvalidOperationException` at runtime |
| H5 | No `ILogger<T>` injection → zero observability | Inject `ILogger<T>` into `SurrealDaliLiveQuery<T>` |
| H6 | No multi-tenancy auto-filter for `TenancyStyle.Conjoined` | Auto-inject `tenant_id` filter in builder |

### MEDIUM (ship in v1.x)

| # | Issue | Fix |
|---|---|---|
| M1 | `LiveQuerySession` doesn't track subscriptions — "dispose kills all" unimplemented | Track subscriptions; dispose cascades to all |
| M2 | Per-subscription channel capacity overrides | Add `.ChannelCapacity(int)` to builder |
| M3 | No `OnOpen(Action)` in builder | Add `.OnOpen(Action)` |
| M4 | `Closed` action has no reason field | Add `ClosureReason?` to `DaliLiveChange<T>` |
| M5 | Naming inconsistency: `ILiveQuerySession` vs `IDaliLiveQueryBuilder<T>` | Decide: drop `Dali` prefix from all types, or add to all types |
| M6 | Old `Watch*` not marked `[Obsolete]` | Add `[Obsolete("Use ILiveQuerySession.Live<T>()")]` |

### LOW (backlog)

| # | Issue |
|---|---|
| L1 | `ExtractId` uses reflection per event — use source-gen accessor |
| L2 | `Changes()` is single-use but appears multi-call — guard against double-enumeration |
| L3 | `.Select()` partial projection leaves unselected fields null/default — document behavior |
| L4 | Broader exception catch in `ReadFromSdkAsync` + structured logging |
| L5 | Guard against unobserved task exceptions (add fire-and-forget error logging) |
| L6 | No `LiveQueryMaxSubscriptions` runaway guard |

---

## 7. Non-Goals (v1)

- Reactive/Observable integration (Rx.NET bridge)
- Server-side `diff` mode (patch-based updates instead of full documents)
- `ListenLive` (re-attach to existing query by UUID)
- Multi-table live queries (joins, graph traversals in live mode)
- Automatic reconnection after WebSocket drop
- `IHostedService` daemon integration for live queries
