# Dali.Reactive — Push-Based Live Query Extensions

## Overview

`Dali.Reactive` is a thin `System.Reactive` (Rx.NET) wrapper around Dali's live query subsystem. It mirrors the pattern established by `SurrealDb.Reactive` — a separate, additive package that bridges from Dali's channel-based `IAsyncEnumerable<T>` pull model to Rx's `IObservable<T>` push model.

**Principle:** additive, non-breaking. The existing `ILiveQuerySession`, `IDaliLiveQuery<T>`, `IDaliLiveQueryBuilder<T>`, and Marten-compatible `WatchTableAsync` paths are untouched.

## Architecture

```
                    ┌─────────────────────────────────┐
                    │     Dali.Reactive (new)          │
                    │                                 │
  IDaliLiveQuery    │  ToObservable()  ──────────────►│  IObservable<DaliLiveChange<T>>
  Builder<T> ───────┤                                 │       │
  .SubscribeAsync() │  SelectResults()                │       ▼
                    │  SelectCreatedRecords<T>()       │  .Where(action != Closed)
                    │  SelectUpdatedRecords<T>()       │  .OfType(change).Select(doc)
                    │  SelectDeletedRecords<T>()       │  .OfType(change).Select(doc)
                    │                                 │
  IDaliLiveQuery<T> │  AggregateRecords<T>(seed)       │  .Aggregate(seed, accumulator)
  .Changes() ───────┤  ScanRecords<T>(seed)           │  .Scan(seed, accumulator)
       │            │                                 │
       ▼            └─────────────────────────────────┘
  IAsyncEnumerable<DaliLiveChange<T>>
  (Channel-based, pull)
```

### Deferred Execution

`ToObservable()` uses `Observable.Defer` + `Observable.Create`. The server-side `LIVE SELECT` query is NOT initiated until a subscriber attaches. Each subscriber gets its own pipeline:

```csharp
// Query is NOT started here:
var obs = store.LiveQuerySessionAsync()
    .Result
    .Live<Person>()
    .Where(p => p.Age > 18)
    .ToObservable();

// Query starts HERE, when first subscriber connects:
var sub = obs.Subscribe(change => Console.WriteLine(change));
```

## API Surface

### `SurrealLiveQueryExtensions` (on `IDaliLiveQueryBuilder<T>`)

| Method | Description |
|--------|-------------|
| `ToObservable(CancellationToken ct)` | Bridges builder's `SubscribeAsync()` → `IObservable<DaliLiveChange<T>>`. Deferred execution — query starts on first subscribe. |

### `ReactiveLinqExtensions` (on `IObservable<DaliLiveChange<T>>`)

| Method | Returns | Description |
|--------|---------|-------------|
| `SelectResults()` | `IObservable<DaliLiveChange<T>>` | Excludes CLOSE notifications |
| `SelectCreatedRecords()` | `IObservable<T>` | Projects only CREATE events' documents |
| `SelectUpdatedRecords()` | `IObservable<T>` | Projects only UPDATE events' documents |
| `SelectDeletedRecords()` | `IObservable<T>` | Projects only DELETE events' documents |

### `StateAccumulatorExtensions` (on `IObservable<DaliLiveChange<T>>`)

| Method | Returns | Description |
|--------|---------|-------------|
| `AggregateRecords(seed)` | `IObservable<IDictionary<string,T>>` | Accumulates CREATE/UPDATE/DELETE into a dictionary. Emits final state on completion. |
| `ScanRecords(seed)` | `IObservable<IDictionary<string,T>>` | Same accumulator but emits after every event (incremental). |

Where `T : Record` for state accumulators (need `.Id` for dictionary key).

## Project Structure

```
src/Dali.Reactive/
  Dali.Reactive.csproj              ← References Dali + SurrealDb.Reactive
  SurrealLiveQueryExtensions.cs     ← ToObservable() on builder
  ReactiveLinqExtensions.cs         ← SelectResults, SelectCreated/Updated/Deleted
  StateAccumulatorExtensions.cs     ← AggregateRecords, ScanRecords

tests/Dali.Tests/
  ReactiveExtensionsTests.cs        ← Unit tests via test constructor path
```

## Dependencies

- `Dali` (project reference)
- `SurrealDb.Reactive` → transitively brings `System.Reactive` (no direct reference needed)
- .NET 10 (same TFMs as `Dali`)

## Usage Examples

### Basic observe
```csharp
await using var store = await TestHarness.CreateStoreAsync();
await using var session = await store.LightweightSessionAsync();

var obs = session.LiveQuerySession()
    .Live<Person>()
    .ToObservable();

using var sub = obs.Subscribe(change =>
    Console.WriteLine($"[{change.Action}] {change.Id}: {change.Document?.Name}"));

// Insert data — subscriber receives live updates
session.Store(new Person { Name = "Alice" });
await session.SaveChangesAsync();
```

### Filter by event type
```csharp
var obs = session.LiveQuerySession()
    .Live<Person>()
    .ToObservable()
    .SelectCreatedRecords();  // only newly created Persons

obs.Subscribe(person => Console.WriteLine($"New person: {person.Name}"));
```

### State accumulation (live cache)
```csharp
var seed = new Dictionary<string, Person>();

store.LiveQuerySession()
    .Live<Person>()
    .ToObservable()
    .ScanRecords(seed)           // emits after every change
    .Subscribe(dict =>
        Console.WriteLine($"Live cache: {dict.Count} persons"));
```

### Aggregate (final state)
```csharp
store.LiveQuerySession()
    .Live<Person>()
    .ToObservable()
    .AggregateRecords(new Dictionary<string, Person>())
    .Subscribe(dict =>
        Console.WriteLine($"Final state: {dict.Count} persons"));
```

## Constraints

- `SurrealDaliLiveQuery<T>` is `internal` — the reactive layer only uses public `IDaliLiveQuery<T>` and `IDaliLiveQueryBuilder<T>` interfaces
- `StateAccumulatorExtensions` requires `T : Record` for the `.Id` property access
- No `InternalsVisibleTo` required — all bridge points are public
- Subscription disposal kills the server-side query via `IDaliLiveQuery.StopAsync()`

## Parity with SurrealDb.Reactive

| Feature | SurrealDb.Reactive | Dali.Reactive |
|---------|-------------------|---------------|
| `ObserveQuery<T>()` | On `ISurrealDbClient` | On `IDaliLiveQueryBuilder<T>.ToObservable()` |
| `ObserveTable<T>()` | On `ISurrealDbClient` | `Live<T>().ToObservable()` |
| `SelectResults()` | ✅ | ✅ |
| `SelectCreatedRecords<T>()` | ✅ | ✅ |
| `SelectUpdatedRecords<T>()` | ✅ | ✅ |
| `SelectDeletedRecords<T>()` | ✅ | ✅ |
| `AggregateRecords<T>()` | ✅ | ✅ |
| `ScanRecords<T>()` | ✅ | ✅ |
| Deferred subscription | `Observable.Defer` | `Observable.Defer` |
| Fluent builder (Where/Select) | ❌ | ✅ (via Dali's `IDaliLiveQueryBuilder<T>`) |
| Marten API parity | ❌ | ✅ (untouched `WatchTableAsync`) |
