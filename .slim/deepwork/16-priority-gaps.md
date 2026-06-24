# 12 Priority Gaps Implementation (16→12 after oracle review)

**Goal**: Implement highest-priority missing features from dali-vs-marten-comparison.md
**Started**: 2026-06-23
**Oracle Verdict**: APPROVED WITH MODIFICATIONS

---
## Oracle Review Summary (Required Changes Applied)

1. ❌ `struct Event<T>` → ✅ `sealed record` — boxing defeats zero-alloc, size > 72 bytes
2. ❌ Change `IProjectionContext.Events` type → ✅ Add `TypedEvents` alongside deprecated `Events`
3. ❌ `Sequence`/`StreamKey` in Phase 7 → ✅ Added to `EventRecord` in Phase 1 scope
4. Merged Phases 3+10 (FetchForWriting + AggregateStreamAsync) — shared `LoadAndApplyAsync<T>`
5. Merged Phases 2+6 (EventProjection + Snapshot<T>) — Snapshot is just EventProjection usage
6. Folded Phase 4 (Evolve/DetermineAction) into Phase 1 — virtual methods on base class
7. Specified global sequence: auto-increment `DEFINE FIELD sequence ... DEFAULT` clause
8. Specified side-effect re-entrancy: re-run inline projections after drain, max depth 10
9. Added `SingleStreamProjection.cs`, `EventRecord`, `TrackingEventStore` to Phase 1 file list

**Net effect**: 16 original phases → 12 merged phases. 3 independent lanes possible after Phase 1.

---
## Research Findings

### Existing Architecture (from code reads + explorer recon)

**Event Store**
- `IEvents`: 4 methods — `FetchStream(string)`, `Append(string, events)`, `StartStream(string, events)`, `FetchAllAfterVersion(long)`
- `EventRecord` DTO (internal): StreamId, Version, EventType, DataJson, CreatedAt (needs +Sequence, +StreamKey in Phase 1)
- `SchemaManager` defines `mt_events SCHEMAFULL`: stream_id, version, event_type, data, created_at
- Events stored as opaque JSON — **no typed deserialization** on fetch
- `DaliEnvelopeEvent` in WolverineFx.Dali wraps `(StreamId, Event, Version)` as `IEvent` (JasperFx) — Wolverine-specific

**Projections**
- Hierarchy: `IProjection` → `InlineProjection<T>` → `SingleStreamProjection<T>` / `MultiStreamProjection<T>`
- `InlineProjection<T>.ApplyAsync()` receives **ALL events as batch** (no per-type dispatch)
- `SingleStreamProjection.GetDocumentId()` uses reflection on event `StreamId` property — needs update for `IEvent`
- `RebuildAsync` clears table, replays all matching events grouped by stream
- No `IEvent<T>` anywhere — events are plain `object`
- **`mt_projection_progress` defined but NEVER WRITTEN TO**

**AsyncDaemon vs SubscriptionDaemon**
- `AsyncDaemon` (core, 160 lines): simple poller, single-threaded, no checkpoint persistence
- `DaliSubscriptionHostedService` (WolverineFx.Dali, 285 lines): Wolverine-integrated with outbox
- Two separate daemon types — no shared `IProjectionDaemon` abstraction

**Session / UoW**
- `SaveChangesAsync` has 5 phases: concurrency checks → version bump → persist entities → **inline projections** (via `TrackingEventStore`) → persist projected docs
- `TrackingEventStore` (nested in `DocumentSession`) wraps `EventStore`, tracks `_appendedEvents`
- No `LoadMany`, `IBatchedQuery`, `CreateBatchQuery`

**Source Generators**
- `DaliDocumentGenerator` — emits `{Type}Metadata.g.cs` for `Record` subclasses (TableName, HasVersion, TenantId accessors)
- No projection-specific source generation currently

**SurrealDB Compatibility** (prior work)
- CBOR: Non-`Record` types fail in embedded — JSON round-trip or extend `Record`
- LET-based Include works in embedded mode — validates IBatchedQuery strategy
- Dahomey.Cbor is case-insensitive — snake_case CBOR → PascalCase C#
- FETCH matches alias name — aliases break FETCH expansion

---
## Revised Phase Plan (12 phases)

```
Phase 1 ──┬── Phase A (EventProjection + Snapshot + Source-gen)
          │
          ├── Phase B (Multi-doc tuple queries)        ← independent
          ├── Phase C (IBatchedQuery)                   ← independent
          ├── Phase D (LoadMany)                         ← independent
          │
          ├── Phase E (FetchForWriting + AggregateStream)  ← depends on Phase 1
          ├── Phase F (Side effects)                       ← depends on Phase 1
          ├── Phase G (Append expectedVersion + Guid)      ← depends on Phase 1
          │
          └── Phase H (Document metadata)                  ← independent
              Phase I (HealthChecks)                        ← independent
              Phase J (Binary serialization)                ← independent
              Phase K (Correlation/Causation)               ← depends on Phase 1
```

### Phase 1: Foundation — `IEvent<T>` Metadata Envelope + Evolve/DetermineAction

**This is the unlock phase.** Everything else depends on it. Folded in former Phase 4 (Evolve/DetermineAction) as virtual methods on `InlineProjection<T>`.

#### Design Decisions (from oracle review)
- `sealed record Event<T>` not `struct` — size > 72 bytes, boxing defeats zero-alloc
- `TypedEvents` added to `IProjectionContext` alongside existing `Events` (zero-breaking-change)
- `Sequence` uses auto-increment DB field — no global counter table needed

#### Changes

**New files:**
- `src/Dali/Events/IEvent.cs` — `IEvent<T>` interface + `IEvent` non-generic alias
- `src/Dali/Events/Event.cs` — `sealed record Event<T> : IEvent<T>`

```csharp
public interface IEvent<out T>
{
    T Data { get; }
    long Version { get; }
    long Sequence { get; }
    DateTimeOffset Timestamp { get; }
    string StreamId { get; }
    Guid StreamKey { get; }
}
public interface IEvent : IEvent<object> { }

public sealed record Event<T>(
    T Data,
    long Version,
    long Sequence,
    DateTimeOffset Timestamp,
    string StreamId,
    Guid StreamKey
) : IEvent<T>;
```

**Modify `EventRecord` (EventStore.cs:158-170):**
- Add: `[Column("sequence")] public long Sequence { get; set; }`
- Add: `[Column("stream_key")] public string StreamKey { get; set; } = "";` (store Guid as string)
- Add: `[Column("timestamp")] public DateTimeOffset Timestamp { get; set; }`

**Modify `SchemaManager.cs` (event schema):**
- Add `DEFINE FIELD sequence ON mt_events TYPE number DEFAULT NONE;` (auto-increment not natively supported — compute in code)
- Add `DEFINE FIELD stream_key ON mt_events TYPE string;`
- Add `DEFINE FIELD timestamp ON mt_events TYPE datetime;`

**Modify `EventStore.Append()` (EventStore.cs:55-80):**
1. Generate `Sequence` = `GetNextSequence()` (query `SELECT count() FROM mt_events GROUP ALL`)
2. Generate `StreamKey` = `Guid.NewGuid()` for new streams, or reuse from existing stream
3. Wrap events in `Event<T>` before storing
4. Populate new record fields (Sequence, StreamKey, Timestamp)

**Modify `EventStore.FetchStream()` (EventStore.cs:22-53):**
- Deserialize `DataJson` to typed object using `EventRecord.EventType`
- Wrap in `Event<object>(data, version, sequence, timestamp, streamId, streamKey)`
- Return `IReadOnlyList<IEvent>`

**Modify `FetchAllAfterVersion()` (EventStore.cs:88-124):**
- Same wrapping as FetchStream
- Return `IReadOnlyList<IEvent>` (tuple format dropped)

**Update `IEvents` interface:**
- All methods return `IReadOnlyList<IEvent>` instead of `IReadOnlyList<object>`
- Old `Append(string, IEnumerable<object>)` signature preserved but wraps internally
- New `Append(string, IEnumerable<IEvent>)` for pre-wrapped events

**Update `IProjectionContext` (IProjectionContext.cs + ProjectionContext.cs):**
```csharp
public interface IProjectionContext
{
    IDocumentSession Session { get; }
    [Obsolete("Use TypedEvents. This property extracts Data from each IEvent.")]
    IReadOnlyList<object> Events { get; }
    IReadOnlyList<IEvent> TypedEvents { get; }
}
```

**Update `InlineProjection<T>` (InlineProjection.cs):**
- `ApplyAsync()` uses `TypedEvents` internally, passes old `Events` (= `.Data`) to existing `ApplyEvents()`
- **NEW virtual methods** (former Phase 4):
  ```csharp
  protected virtual ActionType DetermineAction(T? aggregate, IReadOnlyList<IEvent> events) => ActionType.Store;
  protected virtual T? Evolve(T? aggregate, IReadOnlyList<IEvent> events, CancellationToken ct) => aggregate;
  ```
- `ApplyAsync()` calls `DetermineAction()` first, then `Evolve()` if Store
- New `ActionType` enum: `Store`, `Nothing`, `SoftDelete`, `HardDelete`

**Update `SingleStreamProjection.cs`:**
- `GetDocumentId()`: Replace reflection `evt.GetType().GetProperty("StreamId")` → use `((IEvent)events[0]).StreamId`

**Update `DocumentSession.cs` → `TrackingEventStore`:**
- `_appendedEvents` type changes from `List<(string StreamId, object Event)>` to `List<IEvent>`
- `Append()` wrapper handles new wrapped events

**Update `AsyncDaemon.cs`:**
- `FetchAllAfterVersion` returns `IReadOnlyList<IEvent>`
- Grouping/processing uses `IEvent.StreamId` directly

**Files:** `src/Dali/Events/IEvent.cs` (new), `src/Dali/Events/Event.cs` (new), `src/Dali/Events/EventStore.cs`, `src/Dali/Events/IEvents.cs`, `src/Dali/Events/ActionType.cs` (new), `src/Dali/Projections/IProjectionContext.cs`, `src/Dali/Projections/ProjectionContext.cs`, `src/Dali/Projections/InlineProjection.cs`, `src/Dali/Projections/SingleStreamProjection.cs`, `src/Dali/DocumentSession.cs` (TrackingEventStore), `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Schema/SchemaManager.cs`

**Tests:** ~15 new tests — wrap/unwrap, deserialization round-trip, backward compat, Evolve/DetermineAction lifecycle

---

### Phase A: EventProjection + Snapshot<T> + Source-Gen (merged 2+6)

**Depends on: Phase 1**

**Changes:**

**New file: `src/Dali/Projections/EventProjection.cs`**
```csharp
public abstract partial class EventProjection<T> : InlineProjection<T> where T : Record
{
    // Generated by source-gen OR reflection fallback
    public override T? ApplyEvents(T? aggregate, IReadOnlyList<object> events, CancellationToken ct);
}
```

**Source-gen dispatch (extends `DaliDocumentGenerator`):**
- Scans classes extending `EventProjection<T>`
- Finds `Create(EventType, IEvent<EventType>, CT)` → called when aggregate is null
- Finds `Apply(EventType, T, IEvent<EventType>, CT)` → called for existing aggregate
- Finds `ShouldDelete(EventType, T, IEvent<EventType>, CT)` → returns null to delete
- Generates `ApplyEvents` dispatch switch in `partial class`
- Reflection fallback emits `LogWarning` for unmatched event types

**New extension: `StoreOptions.Projections.Snapshot<T>(config)`**
```csharp
public static void Snapshot<T>(this List<IProjection> projections, Action<SnapshotOptions>? configure = null)
    where T : Record, new()
```
- Registers auto-generated `SnapshotProjection<T>` (implements `EventProjection<T>`)
- Snapshot aggregate applies events to itself via convention `Apply(EventType)` or `When(EventType)`

**New file: `src/Dali/Projections/SnapshotProjection.cs`** (generated partial class)

**Files:** `src/Dali/Projections/EventProjection.cs` (new), `src/Dali.SourceGenerators/DaliDocumentGenerator.cs`, `src/Dali/StoreOptions.cs` (Snapshot extension), `src/Dali/Projections/SnapshotOptions.cs` (new)

**Tests:** ~10 tests — per-event dispatch, Create/Apply/ShouldDelete, reflection fallback, source-gen validation

---

### Phase B: Multi-Doc Tuple Queries (ROW() / Streaming) (independent)

**Depends on: Nothing (Phase 1 not required — operates at SQL level)**

**Changes:**

**New file: `src/Dali/AdvancedSql/DaliAdvancedSql.cs`**
```csharp
public class DaliAdvancedSql
{
    public Task<(T1, T2)> QueryAsync<T1, T2>(string sql, params);
    public Task<(T1, T2, T3)> QueryAsync<T1, T2, T3>(...);
    public IAsyncEnumerable<T> StreamAsync<T>(string sql, params, [EnumeratorCancellation] CT ct);
}
```

- Multi-statement SurrealQL produces results at indices 0, 1, 2 — deserialize each separately
- Tuple types up to 7 elements (like Marten)
- `StreamAsync<T>` uses existing LIVE SELECT infrastructure for IAsyncEnumerable

**Expose via `IDocumentSession`:**
```csharp
public static class DaliAdvancedSqlExtensions
{
    public static DaliAdvancedSql AdvancedSql(this IQuerySession session) => new(session);
}
```

**Files:** `src/Dali/AdvancedSql/DaliAdvancedSql.cs` (new), `src/Dali/AdvancedSql/DaliAdvancedSqlExtensions.cs` (new), `src/Dali/InternalSessionBase.cs`

**Tests:** ~8 tests — 2-tuple, 3-tuple, StreamAsync, error handling

---

### Phase C: IBatchedQuery (independent)

**Depends on: Nothing (reuses LET pattern from Include)**

**Changes:**

**New file: `src/Dali/Batching/IBatchedQuery.cs`**
```csharp
public interface IBatchedQuery
{
    ISurrealDbQueryable<T> Query<T>() where T : class;
    Task ExecuteAsync(CancellationToken ct = default);
}

public interface IBatchedQueryItem
{
    IReadOnlyList<object> Result { get; }
}

public interface IBatchedQueryItem<T> : IBatchedQueryItem
{
    new IReadOnlyList<T> Result { get; }
}
```

- Uses same LET multi-statement pattern as `Include()`:
  ```surql
  LET $q0 = (SELECT * FROM order WHERE Status = 'Pending');
  LET $q1 = (SELECT * FROM customer WHERE Active = true);
  SELECT * FROM $q0;
  SELECT * FROM $q1;
  ```
- Results at indices 0, 1, ...

**Session integration:**
```csharp
var batch = session.CreateBatchQuery();
var orders = batch.Query<Order>().Where(o => o.Status == "Pending");
var customers = batch.Query<Customer>().Where(c => c.Active);
await batch.ExecuteAsync();
var orderList = orders.Result; // IReadOnlyList<Order>
```

**Files:** `src/Dali/Batching/IBatchedQuery.cs` (new), `src/Dali/Batching/BatchedQuery.cs` (new), `src/Dali/Batching/BatchedQueryItem.cs` (new), `src/Dali/IDocumentStore.cs` (CreateBatchQuery), `src/Dali/InternalSessionBase.cs`

**Tests:** ~10 tests — single query, multi query, Where filters, mixed types, empty results

---

### Phase D: LoadMany<T>(ids...) (independent)

**Depends on: Nothing**

**Changes:**

**New extension method on `IQuerySession`:**
```csharp
public static async Task<IReadOnlyList<T>> LoadManyAsync<T>(
    this IQuerySession session, IEnumerable<string> ids, CancellationToken ct = default)
    where T : class
```
- Generates: `SELECT * FROM table WHERE id IN [id1, id2, ...]`
- Use parameterized IDs: `$p0_inline_array` or separate params
- Returns results in order of IDs (or sorted by DB order + client-side reorder)

**Overloads:**
```csharp
Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<RecordId> ids, CT ct)
Task<IReadOnlyList<T>> LoadManyAsync<T>(IEnumerable<long> numericIds, string table, CT ct)
```

**Files:** `src/Dali/LoadManyExtensions.cs` (new), `src/Dali/IQuerySession.cs`

**Tests:** ~5 tests — single ID, multiple IDs, missing IDs, empty list, RecordId overload

---

### Phase E: FetchForWriting<T>() + AggregateStreamAsync<T>() (merged 3+10)

**Depends on: Phase 1**

**Changes:**

**Internal shared helper in `EventStore`:**
```csharp
internal static async Task<T?> LoadAndApplyAsync<T>(
    IQuerySession session, string streamId, CancellationToken ct)
    where T : class, new()
```
1. Load projected document from stream ID
2. Fetch events via `FetchStream(streamId)` → `IReadOnlyList<IEvent>`
3. Apply each event to aggregate via convention or source-gen dispatch
4. Return aggregate

**`FetchForWriting<T>()` — extension on `IDocumentSession`:**
```csharp
public static async Task FetchForWritingAsync<T>(
    this IDocumentSession session, string streamId,
    Func<T, IEvents, Task> handler, CancellationToken ct = default)
    where T : class, new()
```
- Calls `LoadAndApplyAsync<T>` to get current aggregate
- Passes to `handler` which does commands + event appends
- `TrackingEventStore` captures events
- `SaveChangesAsync` commits everything

**`AggregateStreamAsync<T>()` — on `IEvents`:**
```csharp
public Task<T?> AggregateStreamAsync<T>(string streamId, CancellationToken ct = default)
    where T : class, new()
```
- Calls `LoadAndApplyAsync<T>` directly on `IQuerySession`
- Returns aggregate without storing

**Files:** `src/Dali/DocumentSession.cs` (FetchForWriting), `src/Dali/Events/EventStore.cs` (LoadAndApplyAsync, AggregateStreamAsync), `src/Dali/Events/IEvents.cs`

**Tests:** ~8 tests — load+append+save, no events (new aggregate), aggregate with history, AggregateStreamAsync basic

---

### Phase F: Side Effects (RaiseSideEffects) (depends on Phase 1)

**Depends on: Phase 1**

**Design from oracle: re-entrant with max-depth guard**

**Changes:**

**`IProjectionContext` additions:**
```csharp
void RaiseSideEffect(object sideEffect); // queued, not immediate
```

**`ProjectionContext` queue:**
```csharp
internal List<object> SideEffects { get; } = new();
```

**`DocumentSession.SaveChangesAsync` Phase 4 update:**
```
while (sideEffects.Any() && depth < 10)
{
    process side effects (AppendEvent, PublishMessage)
    drain queue
    re-run inline projections on new events
    depth++
}
if (depth >= 10) throw new ProjectionReentrancyException(streamId);
```

**Common side effect types:**
```csharp
public record AppendEvent(string StreamId, object Event) : ISideEffect;
public record PublishMessage(string Channel, string Message) : ISideEffect;
```

**Files:** `src/Dali/Projections/IProjectionContext.cs`, `src/Dali/Projections/ProjectionContext.cs`, `src/Dali/DocumentSession.cs`, `src/Dali/Projections/SideEffects.cs` (new)

**Tests:** ~6 tests — single side effect, chained reactions, max-depth guard, error on overflow

---

### Phase G: Append(expectedVersion) + Guid Stream Identity (merged 9+12)

**Depends on: Phase 1**

**Changes:**

**Guid overloads on `IEvents`:**
```csharp
Task Append(Guid streamId, IEnumerable<object> events, CT ct);
Task<string> StartStream(Guid streamId, IEnumerable<object> events, CT ct);
Task<IReadOnlyList<IEvent>> FetchStream(Guid streamId, CT ct);
```
- Internal: `streamId.ToString("N")` → stored as string in `mt_events.stream_id`
- `IEvent<T>.StreamKey` provides Guid access

**Expected-version append on `IEvents`:**
```csharp
Task Append(string streamId, long expectedVersion, IEnumerable<object> events, CT ct);
Task Append(Guid streamId, long expectedVersion, IEnumerable<object> events, CT ct);
```
- `EventStore` checks `SELECT MAX(version) FROM mt_events WHERE stream_id = $id`
- If != expectedVersion, throws `ConcurrencyException`
- Atomic: version check + append in same transaction

**Files:** `src/Dali/Events/IEvents.cs`, `src/Dali/Events/EventStore.cs`, `src/Dali/Concurrency/ConcurrencyException.cs` (if not exists)

**Tests:** ~8 tests — Guid append/start/fetch round-trip, expectedVersion match, expectedVersion mismatch throws, Guid zero/max edge

---

### Phase H: Document Metadata (last_modified, created_at) (independent)

**Depends on: Nothing**

**Changes:**

**New interface: `IDocumentMetadata`**
```csharp
public interface IDocumentMetadata
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset? LastModified { get; set; }
}
```

**Auto-population via `IDocumentSessionListener`:**
```csharp
public class DocumentMetadataListener : DocumentSessionListenerBase
{
    public override async Task BeforeStoreAsync(IDocumentSession session, object entity, CT ct)
    {
        if (entity is IDocumentMetadata meta)
        {
            if (meta.CreatedAt == default) meta.CreatedAt = DateTimeOffset.UtcNow;
            meta.LastModified = DateTimeOffset.UtcNow;
        }
    }
}
```

**Source-generator updates:**
- Detect `IDocumentMetadata` implementation → flag in generated metadata

**Schema integration (SchemaManager):**
- `DEFINE FIELD created_at ON TABLE {t} TYPE datetime;`
- `DEFINE FIELD last_modified ON TABLE {t} TYPE option<datetime>;`

**Configuration:**
```csharp
o.EnableDocumentMetadata(); // auto-registers listener + adds schema fields
```

**Files:** `src/Dali/Metadata/IDocumentMetadata.cs` (new), `src/Dali/Metadata/DocumentMetadataListener.cs` (new), `src/Dali/StoreOptions.cs`, `src/Dali.SourceGenerators/DaliDocumentGenerator.cs`, `src/Dali/Schema/SchemaManager.cs`

**Tests:** ~5 tests — auto-populated on store, CreatedAt not overwritten, LastModified updated

---

### Phase I: Async Daemon Health Checks (independent)

**Depends on: Nothing (reads AsyncDaemon state)**

**Changes:**

**`AsyncDaemon` exposes health state:**
```csharp
public DaemonHealthState Health { get; }
public record DaemonHealthState(bool IsRunning, DateTimeOffset? LastSuccess, DateTimeOffset? LastError,
    long HighWaterMark, int LagCount, string? LastException);
```

**ASP.NET Core HealthCheck:**
```csharp
public class DaliDaemonHealthCheck : IHealthCheck { ... }
```
- Healthy: daemon running, LastSuccess within 2x poll interval
- Degraded: daemon running but no recent success
- Unhealthy: daemon stopped or repeated failures

**DI registration:**
```csharp
services.AddDali(o => { ... }, healthChecks: true);
// → adds DaliDaemonHealthCheck
```

**Files:** `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Health/DaliDaemonHealthCheck.cs` (new), `src/Dali/DaliServiceCollectionExtensions.cs`

**Tests:** ~3 tests — healthy, degraded, unhealthy states

---

### Phase J: Binary Event Serialization (independent)

**Depends on: Nothing**

**Changes:**

**New enum: `EventSerializationMode`**
```csharp
public enum EventSerializationMode { Json, Binary }
```

**`EventRecord` changes:**
- `DataJson` remains for JSON mode
- Add `[Column("data_binary")] public byte[]? DataBinary { get; set; }` for binary mode
- Serialization mode stored per-record

**Configuration:**
```csharp
o.Events.SerializationMode = EventSerializationMode.Binary;
// → uses MessagePack or System.Text.Json binary for DataBinary
```

**`EventStore.Append()` selects mode based on config.**

**Files:** `src/Dali/Events/EventStore.cs`, `src/Dali/Events/EventSerializationMode.cs` (new), `src/Dali/StoreOptions.cs`

**Tests:** ~4 tests — JSON round-trip, binary round-trip, mode switch, mixed

---

### Phase K: Correlation/Causation Metadata (depends on Phase 1)

**Depends on: Phase 1**

**Changes:**

**Add to `IEvent<T>`:**
```csharp
string? CorrelationId { get; }
string? CausationId { get; }
```

**Add to `EventRecord`:**
```csharp
[Column("correlation_id")] public string? CorrelationId { get; set; }
[Column("causation_id")] public string? CausationId { get; set; }
```

**Add to `Event<T>` (record):**
```csharp
public string? CorrelationId { get; init; }
public string? CausationId { get; init; }
```

**Auto-population in `EventStore.Append()`:**
- `CorrelationId` = `Activity.Current?.Id` (OpenTelemetry) or caller-provided
- `CausationId` = previous event's correlation ID in same stream

**Schema migration:**
```sql
DEFINE FIELD correlation_id ON mt_events TYPE option<string>;
DEFINE FIELD causation_id ON mt_events TYPE option<string>;
```

**Files:** `src/Dali/Events/IEvent.cs`, `src/Dali/Events/Event.cs`, `src/Dali/Events/EventStore.cs`, `src/Dali/Schema/SchemaManager.cs`

**Tests:** ~4 tests — correlation from Activity, causation chaining, null defaults

---

## Implementation Order

1. **Phase 1** (IEvent<T> + Evolve/DetermineAction) — the unlock
2. **Fan-out after Phase 1 passes review:**
   - Lane 1: Phase E (FetchForWriting + AggregateStream) ⸺ | fixer
   - Lane 2: Phase F (Side effects) ⸺ | fixer
   - Lane 3: Phase G (Append expectedVersion + Guid) ⸺ | fixer
   - Lane 4: Phase K (Correlation/Causation) ⸺ | fixer
3. **Independent lanes (can start immediately with Phase 1):**
   - Phase B (Multi-doc tuples) ⸺ | fixer
   - Phase C (IBatchedQuery) ⸺ | fixer
   - Phase D (LoadMany) ⸺ | fixer
   - Phase H (Document metadata) ⸺ | fixer
4. **After fan-out completes:**
   - Phase A (EventProjection + Snapshot + Source-gen) ⸺ needs Phase 1 done
5. **Last:**
   - Phase I (HealthChecks) ⸺ | fixer
   - Phase J (Binary serialization) ⸺ | fixer

**Total phases**: 12. **Independent lanes possible**: 4 after Phase 1.
