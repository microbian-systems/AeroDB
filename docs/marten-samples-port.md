# Marten Samples Port to AeroDB

> Porting 5 Marten-dependent sample projects to use AeroDB as a drop-in replacement.  
> **Last modified:** 2026-07-04

## Council Decisions

These architectural decisions were reached via council review:

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Q1: Projection `T : Record` constraint** | Relax to `T : class`, use POCO identity | POCO support already exists for documents; extending it to projections closes the last gap. ~80-120 lines in `InlineProjection<T>`. |
| **Q2: IProjection API overload** | Add DIM overload | `ApplyAsync(IDocumentOperations, IEnumerable<IEvent>, CancellationToken)` as a default interface method -- costs ~30 lines, gives full Marten parity. |
| **Q3: Porting order** | Sequential, easy-to-hard | Validates the approach at each phase before tackling the hardest project (Helpdesk). |
| **Q4: Kafka/SignalR producers** | Hybrid: add DIM + rewrite producers | DIM gives API parity, rewrite gives cleaner AeroDB-native code. Both are trivial. |

## Prerequisites: Library Changes

Before any sample ports, AeroDB needs two library changes:

### 1. Relax `InlineProjection<T>` constraint (Q1)

**Files:** `src/AeroDB/Projections/InlineProjection.cs`

**Changes:**
- Change `where T : Record` to `where T : class`
- Remove assumption that `result.Id` is `RecordId?`
- Add `IsPoco` detection at construction time via `MetadataDispatch`
- In `ApplyAsync`: replace `result.Id = aggregate?.Id ?? new RecordIdOf<string>(...` with POCO-aware identity assignment using the registered `IdentityProperty` setter
- In `RebuildAsync`: same change
- In `GetDocumentId` paths: handle POCO identity type conversion (Guid, int, long, string)

**Affected subclasses** (need verification):
- `SingleStreamProjection<T>`
- `MultiStreamProjection<T>`
- `EventProjection<T>`
- `SnapshotProjection<T>`

### 2. Add IProjection DIM overload (Q2)

**Files:** `src/AeroDB/Projections/IProjection.cs`

Add a default interface method overload:
```csharp
public interface IProjection
{
    // Existing:
    Task ApplyAsync(IProjectionContext context, CancellationToken ct);

    // New Marten-compatible DIM:
    Task ApplyAsync(IDocumentOperations operations, IEnumerable<IEvent> events, CancellationToken ct)
        => ApplyAsync(new ProjectionContext(operations, events), ct);
}
```

Add a `ProjectionContext` constructor that accepts `IDocumentOperations` + `IEnumerable<IEvent>`.

---

## Phase 0: Extract Shared Types from DaemonTests

> **Status:** Pending  
> **Files to create:** `samples/AeroDB.Samples.Shared/` (new project)

Extract these event types and projection models from the Marten submodule. These are plain C# types -- no Marten dependencies:

**Event types** (from `marten/src/DaemonTests/TestingSupport/`):
- `Arrival.cs`, `Departure.cs`, `Direction.cs`, `Movement.cs`, `Stop.cs`
- `Travel.cs`, `TripAborted.cs` (and `Breakdown`, `VacationOver`)
- `TripEnded.cs`, `TripStarted.cs`
- `IDayEvent.cs`

**Projection documents** (models, no projection logic):
- `Trip.cs` -- convert to POCO with `Guid Id` (remove `IRevisioned`, remove `Activity` base)
- `Day.cs` -- keep as POCO with `int Id`
- `Distance.cs` -- keep as POCO with `Guid Id`

**AeroDB projection classes** (new, AeroDB-native):
- `TripProjection` -- extends `SingleStreamProjection<Trip>` (Q1-enabled)
- `DayProjection` -- extends `MultiStreamProjection<Day>` (Q1-enabled)
- `DistanceProjection` -- extends `EventProjection<Distance>` (Q1-enabled)

Also extract for MinimalAPI:
- `User.cs` -- from `marten/src/Marten.Testing/Documents/User.cs`
- `Target.cs` -- from `marten/src/Marten.Testing/Documents/Target.cs`
- `ConnectionSource.cs` adaptation -- AeroDB equivalent (inline endpoint config)

---

## Phase 1: EventSourcingIntro

> **Status:** Pending  
> **Project:** `samples/EventSourcingIntro/`  
> **Complexity:** Easy  
> **Current refs:** `Marten.csproj` only

### Program.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `DocumentStore.For(options => { ... })` | `Documents.For(options => { ... })` |
| `options.Connection(connectionString)` | `options.Endpoint = "ws://localhost:8000"; options.Namespace = "demo"; options.Database = "demo"` |
| `options.Projections.Add<WarehouseProductProjection>(ProjectionLifecycle.Inline)` | Same API |
| `documentStore.QuerySession()` | `await store.QuerySessionAsync()` |
| `documentStore.LightweightSession()` | `await store.LightweightSessionAsync()` |
| `session.Events.AggregateStreamAsync<T>(id)` | Same API |
| `session.Events.Append(id, events)` | Same API |
| `session.SaveChangesAsync()` | Same API |
| `session.Query<T>().SingleOrDefault(...)` | Same API |

### WarehouseProductProjection Changes
- Change base: `SingleStreamProjection<WarehouseProductReadModel, Guid>` (Marten) to `SingleStreamProjection<WarehouseProductReadModel>` (AeroDB)
- `WarehouseProductReadModel` becomes a POCO registered with `.Identity(x => x.Id)`
- Keep `Apply(ReadModel, Event)` convention methods -- source gen handles dispatch

### WarehouseProductWriteModel Changes
- Already a POCO with `Apply(Event)` methods -- used for live aggregation
- No changes needed; `AggregateStreamAsync<T>()` reflection-based `Apply` works

### WarehouseRepository Changes
- `documentStore.QuerySession()` to `await store.QuerySessionAsync()`
- `session.Query<T>().SingleOrDefault(...)` to Same API

---

## Phase 2: DocSamples

> **Status:** Pending  
> **Project:** `samples/DocSamples/`  
> **Complexity:** Easy  
> **Current refs:** `Marten.Testing.csproj`

### EventSourcingQuickstart.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `DocumentStore.For(_ => { _.Connection(...); })` | `Documents.For(_ => { _.Endpoint = "..."; _.Namespace = "demo"; _.Database = "demo"; })` |
| `store.LightweightSession()` | `await store.LightweightSessionAsync()` |
| `session.Events.StartStream(questId, started, joined1)` | Same API |
| `session.Events.Append(questId, joined2, joined3, arrived)` | Same API |
| `session.Events.FetchForWriting<T>(command.Id)` | Same API |
| `quest.AppendOne(...)` | Same API |
| `session2.Events.AggregateStreamAsync<T>(questId)` | Same API |
| `session2.Events.AggregateStreamAsync<T>(questId, 3)` | Same API |
| `session2.Events.AggregateStreamAsync<T>(questId, timestamp: ...)` | Same API |
| `session.LoadAsync<T>(questId)` | Same API |
| `session.Query<T>().Where(...).ToListAsync()` | Same API |

### RegisteringProjections.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `AddMarten(opts => { ... })` | `AddAeroDB(opts => { ... })` |
| `opts.Connection(...)` | `opts.Endpoint = "..."; opts.Namespace = "demo"; opts.Database = "demo"` |
| `opts.Projections.Add<T>(ProjectionLifecycle.Live)` | Same API |
| `opts.Projections.Add<T>(ProjectionLifecycle.Inline)` | Same API |
| `opts.Projections.Add<T>(ProjectionLifecycle.Async)` | Same API |
| `opts.Projections.Snapshot<T>(SnapshotLifecycle.Inline)` | Same API |
| `opts.Projections.Snapshot<T>(SnapshotLifecycle.Async)` | Same API |
| `opts.Projections.LiveStreamAggregation<T>()` | Same API |

### Projection Type Conversions
- `QuestProjection` -- Marten's `SingleStreamProjection<Quest, Guid>` to AeroDB's `SingleStreamProjection<Quest>`
- `MySpecialProjection` -- Marten bare `EventProjection` to AeroDB `EventProjection<MyDoc>`
- `QuestParty` -- self-aggregating; use AeroDB `Snapshot<T>` or `LiveStreamAggregation`
- All document types become POCOs with `.Identity(x => x.Id)`

### Test Harness
- `ConnectionSource.ConnectionString` to inline `Endpoint`, `Namespace`, `Database` config
- Use `Documents.For()` with embedded SurrealKv for tests

---

## Phase 3: MinimalAPI

> **Status:** Pending  
> **Project:** `samples/MinimalAPI/`  
> **Complexity:** Medium  
> **Current refs:** `Marten.csproj`, `Marten.AspNetCore.csproj`, `DaemonTests.csproj`

### Program.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `builder.Host.ApplyJasperFxExtensions()` | **Remove** |
| `app.RunJasperFxCommands(args)` | `app.RunAsync()` |
| `AddMarten(opts => { ... })` | `AddAeroDB(opts => { ... })` |
| `opts.Connection(ConnectionSource.ConnectionString)` | `opts.Endpoint = "ws://localhost:8000"; opts.Namespace = "cli"; opts.Database = "cli"` |
| `opts.RegisterDocumentType<User>()` | Use POCO: `opts.Schema.For<User>().Identity(x => x.Id)` |
| `opts.DatabaseSchemaName = "cli"` | Use `opts.Namespace` / `opts.Database` |
| `opts.Events.UseArchivedStreamPartitioning = true` | **Remove** (not applicable to SurrealDB) |
| `opts.Schema.For<Target>().SoftDeletedWithPartitioningAndIndex()` | `opts.Schema.For<Target>().SoftDeleted().Identity(x => x.Id)` |
| `opts.Projections.Add(new TripProjectionWithCustomName(), ProjectionLifecycle.Async)` | Same API, use extracted AeroDB projections |
| `opts.Projections.Add(new DayProjection(), ProjectionLifecycle.Async)` | Same API |
| `opts.Projections.Add(new DistanceProjection(), ProjectionLifecycle.Async)` | Same API |

### Project References
- Replace `Marten.csproj` and `Marten.AspNetCore.csproj` with `src/AeroDB/AeroDB.csproj`
- Replace `DaemonTests.csproj` with new `samples/AeroDB.Samples.Shared/` project
- Remove linked code references (`User.cs`, `ConnectionSource.cs`)

---

## Phase 4: AspireHeadlessTripService

> **Status:** Pending  
> **Project:** `samples/AspireHeadlessTripService/`  
> **Complexity:** Medium  
> **Current refs:** `Marten.csproj`, `DaemonTests.csproj`, `EventSourcingTests` linked files

### Program.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `AddMarten(opts => { ... })` | `AddAeroDB(opts => { ... })` |
| `opts.DatabaseSchemaName = "cli"` | `opts.Namespace = "cli"; opts.Database = "cli"` |
| `opts.MultiTenantedWithSingleServer(...)` | AeroDB multi-tenant: `opts.TenancyStyle = TenancyStyle.Conjoined` |
| `opts.Projections.Add(...)` | Same API |
| `.AddAsyncDaemon(DaemonMode.Solo)` | Same API |
| `tracing.AddSource("Marten")` | `tracing.AddSource("AeroDB")` |
| `metrics.AddMeter("Marten")` | `metrics.AddMeter("AeroDB")` |

### Project References
- Replace `Marten/` references with `src/AeroDB/`
- Replace `DaemonTests/` and `EventSourcingTests/` with `AeroDB.Samples.Shared/`
- Remove linked files (`ConnectionSource.cs`)

---

## Phase 5: Helpdesk

> **Status:** Pending  
> **Projects:** `samples/Helpdesk/Helpdesk.Api/` + `samples/Helpdesk/Helpdesk.Api.Tests/`  
> **Complexity:** Hardest  
> **Depends on:** Q1 (POCO projections), Q2 (IProjection overload)

### csproj Changes
- Replace all Marten ProjectReferences (`Marten`, `Marten.AspNetCore`, `Marten.CommandLine`) with `src/AeroDB/AeroDB.csproj`
- Remove `Weasel.Core` references
- Replace `Marten.Pagination` using with AeroDB direct (same API, different namespace)

### Program.cs Changes

| Marten API | AeroDB Replacement |
|-----------|-------------------|
| `builder.Host.ApplyJasperFxExtensions()` | **Remove** |
| `app.RunJasperFxCommands(args)` | `app.RunAsync()` |
| `AddMarten(sp => { var opts = new StoreOptions(); ... })` | `AddAeroDB(opts => { ... })` |
| `options.Events.DatabaseSchemaName` | Use `opts.Namespace` + `opts.Database` |
| `options.Connection(...)` | `opts.Endpoint = "..."; opts.Namespace = "Helpdesk"; opts.Database = "Helpdesk"` |
| `options.UseSystemTextJsonForSerialization(EnumStorage.AsString)` | **Remove** -- AeroDB uses STJ by default |
| `options.Projections.LiveStreamAggregation<Incident>()` | Same API |
| `options.Projections.Add<T>(ProjectionLifecycle.*)` | Same API |
| `options.Projections.Add(instance, lifecycle)` | Same API (Q2 DIM enables Kafka/SignalR) |
| `OptimizeArtifactWorkflow(TypeLoadMode.Static)` | **Remove** |
| `UseLightweightSessions()` | **Remove** or use AeroDB equivalent config |
| `CombGuidIdGeneration.NewGuid()` | `Guid.NewGuid()` |
| `querySession.Json.WriteById<T>(...)` | AeroDB `WriteById<T>` HTTP extension (same API) |
| `querySession.Query<T>().Where(...).WriteArray(context)` | AeroDB `WriteArray<T>` HTTP extension (same API) |
| `session.Events.StartStream<T>(id, event)` | Same API |
| `session.Events.WriteToAggregate<T>(id, ver, handler)` | `FetchForWriting<T>` + `AppendOne` |

### DocumentSessionExtensions.cs Changes
- `Events.StartStream<T>(id, @event)` -- keep (same API)
- `Events.WriteToAggregate<T>(id, ver, handler)` -- replace with:
  ```csharp
  public static async Task GetAndUpdate<T>(
      this IDocumentSession session, Guid id, int version,
      Func<T, object> handle, CancellationToken ct) where T : class
  {
      var result = await session.Events.FetchForWritingAsync<T>(id, ct);
      result.AppendOne(handle(result.Aggregate));
      await session.SaveChangesAsync(cancellationToken: ct);
  }
  ```

### Projection Files
- `SingleStreamProjection<IncidentShortInfo>` -- change base to AeroDB (POCO-enabled via Q1)
- `SingleStreamProjection<IncidentDetails>` -- same
- `EventProjection` (IncidentHistoryTransformation) -- change base to AeroDB `EventProjection<IncidentHistory>`
- `MultiStreamProjection<CustomerIncidentsSummary, Guid>` -- change base to AeroDB `MultiStreamProjection<CustomerIncidentsSummary>`

### KafkaProducer.cs Changes
- Implements AeroDB `IProjection` (using Q2 DIM)
- `StreamAction` iteration replaced with `IProjectionContext.TypedEvents`
- All Kafka-publish logic unchanged

### SignalRProducer.cs Changes
- Same pattern as KafkaProducer -- DIM handles the API, internal logic unchanged

### Incident.cs
- No changes -- plain C# records, already POCOs

### ETagExtensions.cs
- No changes -- ASP.NET infrastructure, not Marten-specific

### Helpdesk.Api.Tests
- No changes to test logic
- References `Helpdesk.Api` transitively; port the API and tests follow
- Replace `FluentAssertions` with `Shouldly` per project conventions

---

## Migration Cheat Sheet

| Marten | AeroDB | Notes |
|--------|--------|-------|
| `DocumentStore.For(...)` | `Documents.For(...)` | Same static factory pattern |
| `opts.Connection(connStr)` | `opts.Endpoint = "ws://..."; opts.Namespace = "..."; opts.Database = "..."` | SurrealDB URL + namespace/database |
| `AddMarten(...)` | `AddAeroDB(...)` | Same DI registration pattern |
| `IDocumentSession` / `IQuerySession` | Same | Identical interface |
| `session.Query<T>()` | Same | Returns `ISurrealDbQueryable<T>` |
| `session.LoadAsync<T>(id)` | Same | Full parity |
| `session.Events.StartStream/AggregateStream/Append` | Same | Full parity |
| `session.Events.WriteToAggregate` | `FetchForWriting` + `AppendOne` | Different API, same semantics |
| `session.Events.FetchForWriting` | Same | Full parity |
| `Projections.Add<T>(lifecycle)` | Same | Full parity |
| `SingleStreamProjection<T>` | Same (after Q1) | AeroDB uses POCO identity instead of stream ID type param |
| `MultiStreamProjection<T>` | Same (after Q1) | Same |
| `EventProjection` (base) | `EventProjection<T>` (after Q1) | AeroDB requires generic type param |
| `IProjection.ApplyAsync` | Same (after Q2 DIM) | DIM overload added |
| `CombGuidIdGeneration.NewGuid()` | `Guid.NewGuid()` | AeroDB handles Guids natively |
| `EnumStorage.AsString` | Default behavior | AeroDB STJ serializes enums as strings |
| `Marten.Pagination.ToPagedListAsync` | `AeroDB.Pagination.ToPagedListAsync` | Same extension method, different namespace |
| `Json.WriteById<T>` / `WriteArray` | Same (AeroDB Http extensions) | Same API surface |
| `ApplyJasperFxExtensions` | **Remove** | CLI tooling only |
| `RunJasperFxCommands` | `app.RunAsync()` | Standard ASP.NET startup |

---

## Dependency Graph

```
Phase 0 (Shared Types)
  |
  v
Phase 1 (EventSourcingIntro) -- validates POCO projections + basic store ops
  |
  v
Phase 2 (DocSamples) -- validates projection lifecycles + snapshot
  |
  v
Phase 3 (MinimalAPI) -- validates DI registration + ASP.NET streaming
  |
  v
Phase 4 (AspireHeadlessTripService) -- validates multi-tenancy + OTel
  |
  v
Phase 5 (Helpdesk) -- validates full CQRS/ES + custom projections + tests
```

Each phase depends on all previous phases succeeding. Start Phase 0 only after Q1 and Q2 library changes are complete and tested.
