# Samples Port: Marten → Dali

## Objective
Convert all Marten-based sample applications in `samples/` to use Dali as the document/event store backend. Upon completion, the `marten/` submodule directory will be removed — no sample may reference Marten assemblies, test infrastructure, or types.

## Reference Sample

**CryptoTrader** (`samples/CryptoTrader/`) is the reference implementation — already converted to Dali + WolverineFx. Use it as the canonical pattern for porting:

- **Project references**: `Dali.csproj` + `Dali.WolverineFx.csproj` (not NuGet)
- **Store bootstrap**: `Documents.For(o => { o.ClientFactory = () => new SurrealDbMemoryClient(); ... })`
- **Event sourcing**: `session.Events.StartStream(id, events)`, `session.Events.Append(id, events)`, `session.Events.AggregateStreamAsync<T>(id)`
- **Graph**: `session.Graph<TNode>()` with Out/In/Both, `session.Relate<TEdge>(from, to, edge)`
- **Wolverine integration**: `Host.CreateDefaultBuilder().UseWolverine(opts => ...)` with `DaliMessageStore`, `DaliOutboxedSessionFactory`, `DaliIntegration`
- **Wolverine sagas**: `TradeSaga` shows Wolverine saga pattern with Dali persistence
- **Side effects**: `IDaliOp` (`DaliOps.Store<T>()`, `DaliOps.Delete<T>()`, `DaliOps.Insert<T>()`) for outbox-safe writes

## Marten → Dali API Mapping

### Store Initialization

| Marten | Dali |
|--------|------|
| `DocumentStore.For(opts => { opts.Connection(cs); })` | `Documents.For(o => { o.ClientFactory = () => new SurrealDbClient(endpoint); }); await store.InitializeAsync();` |
| `builder.Services.AddMarten(opts => ...)` | Manual DI: `services.AddSingleton<IDocumentStore>(store)`; Wolverine handles the rest |
| `opts.DatabaseSchemaName = "x"` | `o.Namespace = "x"; o.Database = "x";` |
| `opts.Events.DatabaseSchemaName = "x"` | Same store — Dali uses Namespace/Database scoping |
| `opts.Schema.For<T>().SoftDeletedWithPartitioningAndIndex()` | `o.Schema.For<T>().SetSchemaMode(SchemaMode.Flexible)`; soft-delete via `session.Delete<T>(entity)` |
| `opts.UseSystemTextJsonForSerialization(...)` | Dali uses System.Text.Json by default |

### Sessions

| Marten | Dali |
|--------|------|
| `store.LightweightSession()` | `await store.LightweightSessionAsync()` |
| `store.QuerySession()` | `await store.QuerySessionAsync()` |
| `store.DirtyTrackedSession()` | `await store.DocumentSessionAsync()` |
| `session.SaveChangesAsync()` | Same API |
| `opts.UseLightweightSessions()` | Dali sessions are lightweight by default |

### Document CRUD

| Marten | Dali |
|--------|------|
| `session.Store<T>(entity)` | Same API |
| `session.Delete<T>(entity)` | Same API |
| `session.LoadAsync<T>(id)` | Same API |
| `session.Query<T>().Where(...).ToListAsync()` | Same API |
| `session.Query<T>().ToPagedListAsync(page, size)` | No direct Dali equivalent — use `Query<T>().Skip().Take()` |
| `session.DeleteWhere<T>(predicate)` | Same API |
| `session.BulkInsertAsync<T>(enumerable)` | Same API |

### Event Sourcing

| Marten | Dali |
|--------|------|
| `session.Events.StartStream(id, event1, event2)` | Same API (id must be string — use `id.ToString()`) |
| `session.Events.Append(id, event1, event2)` | Same API |
| `session.Events.AggregateStreamAsync<T>(id)` | Same API |
| `session.Events.AggregateStreamAsync<T>(id, version)` | Same API |
| `session.Events.AggregateStreamAsync<T>(id, timestamp: dt)` | Same API |
| `session.Events.FetchForWriting<T>(id)` | `session.Events.FetchForWritingAsync<T>(id)` |
| `session.Events.FetchStream(id)` | Same API |
| `opts.Projections.Add<T>(ProjectionLifecycle.Inline)` | `o.Projections.Add<T>(ProjectionLifecycle.Inline)` |
| `opts.Projections.Snapshot<T>(SnapshotLifecycle.Async)` | Flat table projection via `o.Projections.Add<FlatTableProjection<T>>()` |
| `opts.Projections.LiveStreamAggregation<T>()` | No-op — live aggregation is default behavior via `session.Events.AggregateStreamAsync<T>()` |
| `ProjectionLifecycle.Async` | Handled by Wolverine's scheduled job agent (`DaliScheduledJobAgent`) |
| `opts.Events.UseArchivedStreamPartitioning = true` | Not applicable — Dali uses SurrealDB's native table structure |
| `CombGuidIdGeneration.NewGuid()` | `Snowflake.NewId()` (returns `long`) |

### Multi-Tenancy

| Marten | Dali |
|--------|------|
| `opts.MultiTenantedWithSingleServer(cs, t => t.WithTenants("a","b"))` | `store.WithTenant("tenant")` — database-per-tenant |
| `session.SetTenant("tenant")` | Same API |
| No equivalent | `session.ClearTenant()` |

### ASP.NET Core Integration

| Marten | Dali |
|--------|------|
| `querySession.Json.WriteById<T>(id, context)` | No direct equivalent — use `Results.Ok(await session.LoadAsync<T>(id))` |
| `querySession.Query<T>().Where(...).WriteArray(context)` | `Results.Ok(await session.Query<T>().Where(...).ToListAsync())` |
| `Marten.AspNetCore` package | Not needed — Dali integrates via WolverineFx |
| `Marten.CommandLine` package | Not needed — Dali uses `app.RunJasperFxCommands(args)` via Wolverine |
| `AddMarten().OptimizeArtifactWorkflow(...)` | Wolverine handles codegen via `UseWolverine(opts => opts.UseRuntimeCompilation())` |

### Wolverine Integration (already shared via WolverineFx)

| Marten | Dali |
|--------|------|
| `opts.Services.AddMartenStore<IMessageStore>(...)` | Manual: `services.AddSingleton<DaliMessageStore>(); services.AddSingleton<IMessageStore>(sp => ...)` |
| `opts.Services.AddMartenStore<IEventStore>(...)` | Not needed — events are on `IDocumentStore.Events` |
| `IWolverineExtension` | `new DaliIntegration()` registered as singleton |

## Sample Porting Order

### Phase 1 — Simple Console + Test Apps

#### 1. EventSourcingIntro (`samples/EventSourcingIntro/`)
A console app that demonstrates basic event sourcing (warehouse inventory).

**Porting steps:**
- Swap `ProjectReference` from `Marten.csproj` to `Dali.csproj`
- Replace `DocumentStore.For(opts => opts.Connection(...))` with `Documents.For(o => o.ClientFactory = ...)`
- Change `Guid` stream IDs to `string` (or keep and call `.ToString()`)
- Replace `SingleStreamProjection<WarehouseProductReadModel, Guid>` with Dali's projection model (flat table or inline)
- Use `Snowflake.NewId()` instead of `Guid.NewGuid()` for entity IDs
- Add SurrealDB client dependency (`SurrealDb.Embedded.InMemory` for dev, `SurrealDb.Net` for production)
- Result: Same warehouse inventory demo, backed by Dali instead of Postgres/Marten

#### 2. DocSamples (`samples/DocSamples/`)
xunit test project demonstrating event sourcing quickstart and projection registration.

**Porting steps:**
- Swap project references: remove `Marten.Testing` reference
- Convert from xunit to TUnit (`[Test]` → `[Test]`, `Fact` → `[Test]`, remove xunit package refs)
- Swap assertions: `Assert.Equal()` → Shouldly (per AGENTS.md guidelines)
- Replace `DocumentStore.For()` with `Documents.For()` + InMemory client
- Replace `ConnectionSource.ConnectionString` with `new SurrealDbMemoryClient()`
- Adapt projection definitions to Dali's `FlatTableProjection<T>` pattern
- Keep the same event record types — they're transport-agnostic

#### 3. MinimalAPI (`samples/MinimalAPI/`)
ASP.NET Core minimal API with Marten document storage, projections, and soft-delete.

**Porting steps:**
- Swap project references: `Marten.csproj` → `Dali.csproj`, remove `Marten.AspNetCore.csproj`
- Remove `DaemonTests.csproj` reference (Marten test infrastructure — replace with Dali test models)
- Replace `builder.Host.ApplyJasperFxExtensions()` — not needed with Dali/WolverineFx
- Replace `builder.Services.AddMarten(opts => ...)` with manual `IDocumentStore` singleton registration
- Replace `app.RunJasperFxCommands(args)` with `app.RunAsync()` (WolverineFx handles commands internally)
- Replace controller-based Marten injection with Dali session + WolverineFx message dispatch
- Move projection event types and projection classes inline (no longer referencing DaemonTests)
- Define Dali event + document models directly in the sample project

### Phase 2 — Telemetry + Orchestrator Apps

#### 4. AspireHost + AspireHeadlessTripService
Aspire orchestrator launching a headless trip service with OpenTelemetry + async projections.

**Porting steps:**
- AspireHost is minimal — no changes needed (orchestrates other projects)
- AspireHeadlessTripService: swap Marten.csproj → Dali.csproj
- Remove DaemonTests.csproj reference (TripProjectionWithCustomName, DayProjection, DistanceProjection)
- Implement equivalent Dali projections (FlatTableProjection<T> or inline event handlers)
- Keep OpenTelemetry setup identical — Dali uses `System.Diagnostics.Activity` sources
- Replace `ConnectionSource.ConnectionString` with Dali connection config
- Replace `AddAsyncDaemon(DaemonMode.Solo)` with Wolverine's scheduled job agent (configured in `UseWolverine(opts => ...)`)

### Phase 3 — Full CQRS App (Helpdesk)

#### 5. Helpdesk (`samples/Helpdesk/`)
Full event-sourced incident management system with Kafka, SignalR, projections, ETag concurrency.

**Porting steps:**
- Swap Marten references in `Helpdesk.Api.csproj` for Dali project references
- Adapt `Program.cs`:
  - Replace `AddMarten(sp => ...)` with direct `IDocumentStore` registration
  - Remove `UseLightweightSessions()` — Dali default
  - Remove `OptimizeArtifactWorkflow()` — Wolverine handles codegen
  - Replace `AddAsyncDaemon(DaemonMode.Solo)` with Wolverine scheduled agent
  - Keep `AddSignalR()` and `AddCors()` as-is
- **Incident aggregate** (`Incident.cs`):
  - Replace `CombGuidIdGeneration.NewGuid()` with `Snowflake.NewId()`
  - Adapt the event-sourced aggregate to Dali's `IEvent` pattern
  - Map `IncidentService.Handle()` methods to Dali event appends
- **Projections**: Replace Marten projection types:
  - `IncidentHistoryTransformation`, `IncidentDetailsProjection`, `IncidentShortInfoProjection`, `CustomerIncidentsSummaryProjection` → Dali projections
  - `KafkaProducer` projection → keep as inline projection with Kafka dispatch
  - `SignalRProducer` projection → keep as inline projection with SignalR dispatch
- **ETag/Concurrency**:
  - Replace `GetAndUpdate<T>(id, expectedVersion, handler, ct)` with Dali's versioned session operations
  - Replace `[FromIfMatchHeader]` attribute with custom ETag middleware
- **HTTP endpoints**:
  - Replace `querySession.Json.WriteById<T>(id, context)` with `Results.Ok(await querySession.LoadAsync<T>(id))`
  - Replace `querySession.Query<T>().Where(...).ToPagedListAsync()` with `Query<T>().Where(...).Skip().Take().ToListAsync()`
  - Replace `querySession.Query<T>().Where(...).WriteArray(context)` with `Results.Ok(await querySession.Query<T>().Where(...).ToListAsync())`
- **Tests** (`Helpdesk.Api.Tests`):
  - Convert from xunit to TUnit
  - Replace `FluentAssertions` with Shouldly
  - Replace Alba-based Marten integration with Dali InMemory store
  - Keep Ogooreck for HTTP scenario testing

## Docker for Integration Testing

The existing `marten/docker/postgres/` Dockerfile is Postgres-specific — **not used by any of the ported samples**. The 3 simple apps (EventSourcingIntro, DocSamples, MinimalAPI) and AspireHeadlessTripService use `SurrealDbMemoryClient` (embedded, no Docker needed).

### Helpdesk Docker Setup

The Helpdesk app does **not** use Aspire. It has its own `docker-compose.yml` at `samples/Helpdesk/docker-compose.yml` with:

| Service | Purpose | Porting Action |
|---------|---------|---------------|
| **Postgres** (plv8) | Marten database | ❌ Remove — replace with `surrealdb/surrealdb` |
| **pgadmin** | Postgres admin | ❌ Remove — replace with `surrealdb/surrealist` (SurrealDB web UI) |
| **Zookeeper** | Kafka coordination | ✅ Keep |
| **Kafka** | Message bus for projection | ✅ Keep |
| **Schema Registry** | Avro schema | ✅ Keep |
| **Kafka REST** | Kafka HTTP proxy | ✅ Keep |
| **Kafka Topics UI** | Kafka management UI | ✅ Keep |

Porting: Postgres service replaced with `surrealdb/surrealdb:latest`. pgadmin replaced with `surrealdb/surrealist:latest`. Zookeeper + Kafka + Schema Registry remain for the Kafka-based `KafkaProducer` projection.

### New docker structure (for future use)

```
docker/surrealdb/
├── docker-compose.yml    # Minimal SurrealDB-only (for manual testing)
└── README.md
```

### SurrealDB Docker Compose (standalone, for apps without Kafka)

```yaml
version: "3"
services:
  surrealdb:
    image: surrealdb/surrealdb:latest
    ports:
      - "8000:8000"
    command: start --log trace --user root --pass root
```

## Porting Checklist

For each sample app:

- [ ] `csproj`: Marten references removed, Dali project references added
- [ ] `Program.cs`/entry point: `DocumentStore.For()` → `Documents.For()` + `InitializeAsync()`
- [ ] Connection config: Postgres connection string → SurrealDB endpoint + InMemory fallback
- [ ] ID generation: `Guid.NewGuid()` or `CombGuidIdGeneration` → `Snowflake.NewId()`
- [ ] Event sourcing: `StartStream/Append/AggregateStreamAsync/FetchForWriting` adapted
- [ ] Projections: `SingleStreamProjection<T>` → Dali's projection model or inline
- [ ] Sessions: sync → async (`LightweightSession()` → `await LightweightSessionAsync()`)
- [ ] DI registration: `AddMarten()` → manual `IDocumentStore` + `DaliMessageStore` + `DaliOutboxedSessionFactory`
- [ ] Wolverine: `UseWolverine(opts => ...)` with `DaliIntegration` registered
- [ ] `app.RunJasperFxCommands(args)` → `app.RunAsync()`
- [ ] `builder.Host.ApplyJasperFxExtensions()` → remove (covered by Wolverine)
- [ ] `Marten.AspNetCore` JSON HTTP helpers → standard `Results.Ok()` / `TypedResults`
- [ ] Remove `DaemonTests` references — replace inline models
- [ ] Test projects: xunit → TUnit, `FluentAssertions` → Shouldly
- [ ] Verify: compile + run with `SurrealDbMemoryClient` (dev) and SurrealDB Docker (integration)

## Non-Goals

- Marten's `IInitialData` seeding — use Dali bootstrapping instead
- Marten's `IDocumentSessionListener` — use Wolverine middleware or `IDocumentSession` callbacks
- Marten's patch/partial update API — Dali uses full document replacement
- Marten's compiled query feature — Dali uses `ICompiledQuery` with different internals
- Marten's `MetadataColumn` and table partitioning — not applicable to SurrealDB
