# Marten vs Dali — API Gap Analysis

Audit of Marten's API surface (from `docs/marten-llms-full.txt`) against Dali's current implementation (from `src/Dali/` and `src/Dali.WolverineFx/`).

---

## 🔴 Critical Gaps (block Marten migration)

### 1. `FetchForWriting` / aggregate version-tracking pattern

**Marten**: `session.Events.FetchForWriting<T>(id)` loads the aggregate document, tracks the expected stream version, and lets callers `quest.AppendOne(new Event(...))`. On `SaveChangesAsync`, Marten verifies the stream version matches and throws `EventStreamUnexpectedMaxEventIdException` on conflict.

**Dali**: No equivalent. Callers must manually `FetchStream(streamId)`, track the `Version` from the last event, then call `Append(streamId, expectedVersion, events)`. Error-prone for write-model workflows.

**Files affected**: `src/Dali/IEvents.cs`, `src/Dali/EventStore.cs`, `src/Dali/DocumentSession.cs`

---

### 2. `AggregateStream<T>()` on session

**Marten**: `session.Events.AggregateStreamAsync<T>(id, version?)` — single call to load all events for a stream and fold them into the aggregate type via convention-based `Apply`/`Create` methods.

**Dali**: `LiveStreamAggregation.AggregateAsync<T>()` exists as a static helper but is not surfaced on `IEvents` or `IQuerySession` directly.

**Files affected**: `src/Dali/Events/LiveStreamAggregation.cs`, `src/Dali/IEvents.cs`

---

### 3. Projection progress persistence

**Marten**: The async daemon persists projection high-water marks (sequence numbers) to `mt_projection_progress` table. On restart, the daemon resumes from the last persisted position.

**Dali**: `mt_projection_progress` table schema is defined in `SchemaManager.cs:126-134` and created during `InitializeAsync`, but neither the `AsyncDaemon` nor `ProjectionContext` ever reads or writes to it. The high-water mark is **in-memory only** (`_highWaterSequence` field in `AsyncDaemon`). A process restart causes full re-projection from event 0.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 4. `IgnoreIndex` / `IgnoreIdentifier`

**Marten**: `Schema.For<T>().IgnoreIndex("index_name")` excludes a database index from schema delta computation. `IgnoreIdentifier` marks properties as not part of the document identity.

**Dali**: **Not implemented.** No mechanism to skip index detection or exclude members from identifier convention.

**Files affected**: `src/Dali/Schema/DocumentMapping.cs`, `src/Dali/Schema/SchemaOptions.cs`

---

### 5. `MetadataColumn` / `MetadataConfig`

**Marten**: `Schema.For<T>().Metadata(m => { m.Version.MapTo(c => c.MyVersionCol); m.TenantId.MapTo(c => c.TenantCol); })` — fluent API to control which metadata fields are stored, where (column mapping), and their visibility in document tables.

**Dali**: Only `IDocumentMetadata` with 3 fixed properties (`CreatedAt`, `LastModified`, `LastModifiedBy`), auto-populated by `DocumentMetadataListener`. No configurable column placement, no dotnet-type column, no way to opt metadata fields in/out per document type.

**Files affected**: `src/Dali/Metadata/IDocumentMetadata.cs`, `src/Dali/Diagnostics/DocumentMetadataListener.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 6. `IDocumentStore.Advanced` API severely limited

**Marten**: `store.Advanced` exposes ~20+ admin methods: `Clean.CompletelyRemoveAsync()`, `Clean.DeleteAllDocumentsAsync()`, `Clean.DeleteAllEventDataAsync()`, `Clean.DeleteDocumentsByTypeAsync()`, `Clean.DeleteDocumentsExceptAsync()`, `CleanDeletedDocumentsAsync()`, `AllProjectionProgress()`, `FetchEventStoreStatistics()`, `ProjectionProgressFor(ShardName)`, `ResetAllData()`, `AddMartenManagedTenantsAsync()`, `DeleteAllTenantDataAsync()`, `RemoveMartenManagedTenantsAsync()`, `AddTenantToShardAsync()`, `AddDatabaseToPoolAsync()`, `MarkDatabaseFullAsync()`, `ApplyEventDataMasking()`.

**Dali**: `IDaliAdvanced` (at `store.Advanced`) has only 2 members: `Client` (ISurrealDbClient) and `CreateSessionAsync()`. No Clean API, no diagnostics, no tenant management, no event masking, no DB pool management. 18+ methods missing.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentStore.cs`

---

### 7. `IDocumentStore.Diagnostics` / query diagnostics

**Marten**: `store.Diagnostics.PreviewCommand(compiledQuery)`, `store.Diagnostics.ExplainPlan(compiledQuery)`, `store.Diagnostics.GetPostgresVersion()`. Also `queryable.ToCommand(FetchType)` and `queryable.ExplainAsync()` on queryables.

**Dali**: No way to preview generated SurrealQL or get explain plans. `ToCommand()` exists on `ISurrealDbQueryable<T>` but there's no store-level diagnostics API.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/Linq/SurrealQueryProvider.cs`

---

## 🟡 Major Gaps

### 8. Event causation (CorrelationId / CausationId / Headers)

**Marten**: Every `IEvent` carries `CorrelationId`, `CausationId`, and `Headers` (dictionary) for tracing event chains across streams. CausationId references the event that triggered this one; Headers carry arbitrary metadata.

**Dali**: `IEvent` / `IEvent<T>` only exposes `StreamId`, `StreamKey`, `Version`, `Sequence`, `Timestamp`, `Data`. No `CorrelationId`, `CausationId`, or `Headers`. The `DaliEnvelopeEvent` wrapper in WolverineFx has these, but only in the Wolverine pipeline.

**Files affected**: `src/Dali/Events/IEvent.cs`, `src/Dali/Events/Event.cs`

---

### 9. `IChangeSet` before/after values

**Marten**: `IChangeSet.Changes()` returns `IChange<T>` where each change has `.Before` and `.After` snapshots of the document, enabling precise change tracking and audit logging.

**Dali**: `IChangeSet` (in `Diagnostics/IChangeSet.cs`) only provides `IReadOnlyList<Operation> Operations` and `IReadOnlyList<(string StreamId, object Event)> AppendedEvents`. No before/after snapshots.

**Files affected**: `src/Dali/Diagnostics/IChangeSet.cs`, `src/Dali/Diagnostics/ChangeSet.cs`

---

### 10. `ForeignKey` constraints

**Marten**: `Schema.For<T>().ForeignKey<TRef>(x => x.RefId)` generates a foreign key constraint in PostgreSQL.

**Dali**: **Not implemented.** SurrealDB has no native FK constraint concept (references are expressed via record IDs and graph edges), but there is no API to declare referential rules at the mapping level.

**Files affected**: `src/Dali/Schema/DocumentMapping.cs`

---

### 11. `ComputedIndex` with custom SQL / index options

**Marten**: `Index(x => x.Number, c => { c.Method = IndexMethod.brin; c.Casing = Casings.Lower; c.SortOrder = SortOrder.Desc; c.Predicate = "(data ->> 'Number')::int > 10"; })` — full control over index method, casing, sort order, and partial index predicates.

**Dali**: `IndexOptions` only supports `IsUnique()` and `WithName()`. No `Method`, `Casing`, `SortOrder`, or `Predicate`. SurrealDB only supports btree, so some options (like brin) are SurrealDB-irrelevant, but no-op stubs are needed for Marten migration compatibility.

**Files affected**: `src/Dali/Schema/IndexDefinition.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 12. `CleanDeletedDocumentsAsync()`

**Marten**: `IDocumentStore.Advanced.CleanDeletedDocumentsAsync()` bulk-purges all soft-deleted records.

**Dali**: **Not implemented.** No way to programmatically purge soft-deleted data. A manual raw SurrealQL query is required.

**Files affected**: None — new feature needed.

---

### 13. `DeletedBefore()` query extension

**Marten**: `session.Query<T>().Where(x => x.DeletedBefore(datetime))` — temporal filter on deletion timestamp in LINQ queries.

**Dali**: **Not implemented.** No LINQ expression support for filtering by `DeletedAt` or `Deleted` status in the query provider.

**Files affected**: `src/Dali/Linq/SurrealExpressionVisitor.cs`

---

### 14. AsyncDaemon persisted watermark

**Marten**: The async daemon persists per-shard sequence numbers to `mt_projection_progress` table, allowing resume-after-restart without full replay.

**Dali**: The `mt_projection_progress` table is defined and created but never written to. The daemon tracks position only in memory. Table read/write plumbing is entirely missing.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 15. `Transform` / JavaScript transformations (PLV8)

**Marten**: Supports event transformations via `Transform` and PLV8 (JavaScript within PostgreSQL).

**Dali**: **Not applicable and not implemented.** SurrealDB has no equivalent of PLV8. No transformation pipeline exists.

**Files affected**: None — not a goal for SurrealDB parity.

---

### 16. `SchemaDiff` / `SchemaPatch` (migration API)

**Marten**: `IDocumentStore.Schema` exposes schema diffing and patch generation for database migrations.

**Dali**: **Not implemented.** No schema comparison, diff, or migration generation API. Schema is applied eagerly via `InitializeAsync()`.

**Files affected**: None — new feature needed.

---

### 17. Session types / identity map / dirty tracking

**Marten**: Three session types: `LightweightSession` (no tracking), `DirtyTrackedSession` (identity map + auto-diff on save), `QuerySession` (read-only). `SessionOptions` class configures `DocumentTracking` mode (None / IdentityOnly / DirtyTracking), isolation level, and listeners per session. `OpenSession(SessionOptions)` is the unified factory.

**Dali**: Three factory methods exist (`LightweightSessionAsync`, `DocumentSessionAsync`, `QuerySessionAsync`) but `DocumentSessionAsync` is identical to `LightweightSessionAsync` — the `_isDirtyTracking` field is never read (dead code). `IdentityMap` dictionary exists on `InternalSessionBase` but is never populated by `LoadAsync<T>`. No `SessionOptions` class, no `DocumentTracking` enum, no auto-dirty-detection on `SaveChangesAsync`. `OpenSessionAsync(opts)` factory missing.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentSession.cs`, `src/Dali/InternalSessionBase.cs`, `src/Dali/StoreOptions.cs`

---

### 18. `SubClass` / type hierarchy on `Schema.For<T>()`

**Marten**: `Schema.For<T>().AddSubClass<TDerived>()` — declarative hierarchy setup per document mapping.

**Dali**: Uses `StoreOptions.HierarchyFor<TBase>()` at the store level instead. Different API shape — works for the same goal but is a migration hurdle.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/DocumentHierarchy.cs`

---

### 19. `Policies` system / `IDocumentPolicy`

**Marten**: `StoreOptions.Policies.ForAllDocuments(Action<DocumentMapping>)`, `Policies.ForDocumentsOfType<T>(Action<DocumentMapping>)`, `Policies.UseOptimisticConcurrency()`, `Policies.SetAllProperties()`, custom `IDocumentPolicy` plugin interface.

**Dali**: Only `StoreOptions.UseOptimisticConcurrency` as a `bool`. No `IDocumentPolicy`, no `ForAllDocuments()`, no `SetAllProperties()`, no plugin system.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 20. Session-level listeners

**Marten**: `SessionOptions.Listeners` — listeners attached per-session via `store.OpenSession(new SessionOptions { Listeners = { new MyListener() } })`.

**Dali**: Only global `StoreOptions.Listeners`. No per-session listener injection.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentStore.cs`, `src/Dali/StoreOptions.cs`

---

### 21. `IBatchedQuery` raw SQL

**Marten**: `batch.Query<T>(sql, parameters)` — batch executes raw SQL strings alongside compiled queries.

**Dali**: `IBatchedQuery` only supports `Query<TDoc, TOut>(ICompiledQuery)`. No raw SQL overload.

**Files affected**: `src/Dali/IBatchedQuery.cs`, `src/Dali/BatchedQuery.cs`

---

### 22. `IDocumentOperations` interface

**Marten**: `IDocumentOperations` is the write-side parent of `IDocumentSession`, used as the parameter type in projection methods `ApplyAsync(IDocumentOperations, IEvent, CancellationToken)` and `Project(event, IDocumentOperations)`.

**Dali**: `IProjection.ApplyAsync(IProjectionContext ctx)` gives `IDocumentSession Session` but no `IDocumentOperations` interface exists. Projection API is incompatible with Marten conventions.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/Projections/`

---

### 23. Projection sharding

**Marten**: Async daemon partitions work per-projection across multiple shards (`IProjectionSource`, `IProjectionShard`, `ShardName`). Each shard has independent progress tracking.

**Dali**: Async daemon is single-threaded — no sharding, no `IProjectionSource`, no `IProjectionShard`, no `ShardName`.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`

---

### 24. `CustomGrouping` / `IAggregateGrouper<TId>`

**Marten**: `MultiStreamProjection.CustomGrouping(IAggregateGrouper<TId>)`, `CustomGrouping(Func<IQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task>)`, and `IEventSlicer` for custom event-to-aggregate grouping.

**Dali**: `MultiStreamProjection<T>` only has `GetDocumentId(IReadOnlyList<object>)`. No custom grouping, grouper, or slicer.

**Files affected**: `src/Dali/Projections/MultiStreamProjection.cs`

---

### 25. Composite projections

**Marten**: `opts.Projections.CompositeProjectionFor("Name", x => x.Add<A>().Add<B>().Add<C>())` — one projection that composites multiple sub-projections.

**Dali**: Not implemented. No composite projection support.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Projections/`

---

### 26. `SessionOptions` class

**Marten**: Rich `SessionOptions` with `Tracking` (DocumentTracking), `Timeout`, `Listeners[]`, `IsolationLevel`, `TenantId`, `Connection`, and static factory methods: `ForConnectionString()`, `ForTransaction()`, `ForCurrentTransaction()`.

**Dali**: No `SessionOptions` class. Session factory methods take only `CancellationToken`.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentStore.cs`

---

### 27. `IDocumentStore.BulkInsertAsync` + `BulkInsertMode`

**Marten**: `store.BulkInsertAsync<T>(entities, mode)` at store level. `BulkInsertMode` enum: `InsertsOnly`, `IgnoreDuplicates`, `OverwriteExisting`, `OverwriteIfVersionMatches`. Tenant-aware overload.

**Dali**: Only `BulkInsertAsync<T>()` as static extension method on `IDocumentSession`. No `BulkInsertMode` enum, no store-level API, no tenant overload.

**Files affected**: `src/Dali/BulkOperations.cs`, `src/Dali/IDocumentStore.cs`

---

## 🟢 Minor Gaps

| # | Gap | Marten | Dali |
|---|-----|--------|------|
| 28 | **`ISubscriber` / `IChangeListener`** | External event bus subscription model | Not present — uses Wolverine integration or `IDaliSubscription` instead |
| 29 | **`StoreOptions.Serializer()`** | Custom serializer configuration (JSON.NET, STJ) | Hardcoded `System.Text.Json` with snake_case; no user-facing config |
| 30 | **`BulkInsert` on `IDocumentSession`** | `session.BulkInsert<T>(entities)` as interface method | `BulkInsertAsync` is a **static extension** method in `BulkOperations`, not on the interface |
| 31 | **Hard-delete from query** | `session.Query<T>().Where(...).Delete()` | Only entity-based `Delete<T>(entity)` — no query-delete |
| 32 | **Soft-delete field naming** | Shadow columns `mt_deleted`, `mt_deleted_at` | Entity properties via `ISoftDeleted.Deleted` / `DeletedAt` directly |
| 33 | **`IdentityMap` diagnostics** | `IDocumentSession.Database` / `DocumentTracking` tracking modes | `_identityMap` exists on `InternalSessionBase` but no diagnostic/exposure API |
| 34 | **`ICompiledQuery` on `IDocumentStore`** | `store.QueryAsync<TDoc, TOut>(compiled)` | Only on `IQuerySession`, not on `IDocumentStore` |
| 35 | **Projection rebuild progress** | `RebuildAsync` with progress reporting | `RebuildAsync` runs as blocking operation with no progress callback |
| 36 | **Custom projection names** | User-assignable name on `IProjection` | Projections identified by `GetType().Name` only |
| 37 | **Live projection lifecycle** | Async daemon processes all lifecycles | Daemon explicitly filters `Where(p => p.Lifecycle == Async)` — Live projections never processed |
| 38 | **`FlatTableProjection.RebuildAsync`** | Full rebuild support | Returns `Task.CompletedTask` — **stub**, not implemented |
| 39 | **`BulkInsertEventsAsync`** | `store.BulkInsertEventsAsync(streams, batchSize)` — bulk-insert entire event streams at store level | Events appended one stream at a time |
| 40 | **Per-session logger swap** | `IMartenSessionLogger` swappable per-session; `session.Logger = new RecordingLogger()` | Only `ILoggerFactory` on `StoreOptions` (global) |
| 41 | **`RequestCount` on sessions** | `session.RequestCount` — number of DB commands issued by that session | Not implemented |
| 42 | **Event data masking (GDPR)** | `store.Advanced.ApplyEventDataMasking(Func<IEvent, bool>)` with per-event-type redaction | Not implemented |
| 43 | **`IChangeListener`** | Async daemon pipeline listener with `BeforeCommitAsync`/`AfterCommitAsync` | Only `IDocumentSessionListener` |
| 44 | **`IEventSlice<T>` / enrichment** | `IEventSlice<T>` with `Aggregate`, `Id`, `Events`; `IProjectionEnrichment` hooks | Only `IProjectionContext` with `Events` + `Session` |

---

## ✅ At Parity — Confirmed Implemented

| Area | Status |
|------|--------|
| Document sessions (`IDocumentSession` / `IQuerySession`) | ✅ |
| Fluent schema `Schema.For<T>()` with all index types | ✅ |
| Anonymous-type multi-column indexing `Index(x => new { ... })` | ✅ |
| LINQ querying | ✅ |
| `IPatchExpression<T>` — `Set`, `Increment`, `Append`, `Insert`, `Remove`, `Rename`, `Delete` | ✅ |
| `ICompiledQuery<TDoc, TOut>` interface-based compiled queries | ✅ |
| `IBatchedQuery` for multi-statement round-trips | ✅ |
| Multi-tenancy (Conjoined + DatabasePerTenant) | ✅ |
| `IDocumentSessionListener` hooks | ✅ |
| Soft deletes via `ISoftDeleted` + auto-query-filtering | ✅ |
| Event sourcing core: `IEvents`, `Append`, `FetchStream`, `StartStream`, `ArchiveStream` | ✅ |
| Event upcasting | ✅ |
| `InlineProjection`, `SingleStreamProjection`, `MultiStreamProjection`, `SnapshotProjection` | ✅ |
| `FlatTableProjection` | ✅ |
| `IProjectionSideEffect` / `AppendEventSideEffect` | ✅ |
| `IDocumentStore.Daemon` background processing | ✅ |
| `IInitialData` seeding | ✅ |
| `IConfigureDali` / `IAsyncConfigureDali` auto-discovery | ✅ |
| Live queries (SurrealDB `LIVE SELECT`) | ✅ |
| Graph traversal / edge management | ✅ |
| Optimistic concurrency (`IVersioned`, `[Version]`) | ✅ |
| Schema for FULLTEXT, HNSW, MTREE, DISKANN, Geo indexes | ✅ |
| FTS analyzer management + BM25 scoring | ✅ |
| `DEFINE TABLE AS SELECT` (pre-computed views) | ✅ |
| `DEFINE FUNCTION` (user-defined functions) | ✅ |
| `DEFINE EVENT` (SurrealDB triggers) | ✅ |
| Event serialization modes (JSON / Binary) | ✅ |
| Event append modes (Rich / Quick) | ✅ |
| Snowflake ID generation | ✅ |
| Source-generated metadata (`ITypeMetadata<T>`) | ✅ |

---

## Test Coverage Gaps

The following areas lack robust tests and need dedicated test suites:

### Event Sourcing

- **Stream lifecycle**: Start → Append → Fetch → Archive → Tombstone
- **Event replay**: `AggregateStreamAsync` (or manual `FetchStream` + fold) for a stream of 1000+ events
- **Version conflict detection**: `Append(streamId, expectedVersion, events)` with wrong version throws `ConcurrencyException`
- **Causation chain**: Trace event chains across streams (once CorrelationId/CausationId are implemented)
- **Multi-stream projections**: `MultiStreamProjection<T>` with events across multiple streams aggregating into a single document
- **Inline projection lifecycle**: Event appended → `SaveChangesAsync` → inline projection executes → projected document updated
- **Async daemon**: `AsyncDaemon.Start()` → processes events → calls `IProjection.ApplyAsync` → advances watermark
- **Daemon restart idempotency**: Daemon stops mid-batch, restarts, processes same events again (once persistence is added)
- **Upcaster pipeline**: Old-format event → upcaster transforms → new-format event applied to projection
- **Live aggregation**: `LiveStreamAggregation.AggregateAsync<T>()` on a stream with multiple event types

### Projections

- **InlineProjection.Evolve()**: Returns `null` → document deleted. Returns updated snapshot → document stored.
- **FlatTableProjection**: Event data mapped to flat columns correctly
- **SnapshotProjection**: `Apply`/`When` convention dispatch fires correctly for each event type
- **Projection side effects**: `RaiseSideEffect` appends new events during projection processing
- **Rebuild correctness**: `RebuildAsync()` produces the same result as processing events sequentially

### Patching

- **Deferred execution**: Patches queued on session execute during `SaveChangesAsync`, not before
- **Filtered patches**: `Patch<T>(expression.Where(...))` only updates matching records
- **Concurrent patches**: Two patches against the same document in one session
- **Patch + regular store**: `session.Store(entity)` then `session.Patch<T>(id).Set(...)` in same unit of work

### Soft Delete

- **Auto-filtering**: `Query<T>()` on `ISoftDeleted` type automatically excludes `Deleted == true`
- **Undelete**: `session.Store(entity)` on soft-deleted entity clears `DeletedAt` and `Deleted`

### Compiled Queries

- **Interface-based**: `ICompiledQuery<TDoc, TOut>.QueryIs()` executes correctly
- **Pre-compiled**: `store.CompileQuery<T>(expr)` × `session.QueryAsync(compiled)` returns correct results
- **Batched**: `IBatchedQuery` with multiple compiled queries executes in single round-trip
- **Tenant-aware**: Compiled query auto-appends `TenantId` filter in Conjoined multi-tenancy

### Schema & Indexing

- **Schema idempotency**: `InitializeAsync()` called twice does not error or duplicate indexes
- **Index naming**: Custom `WithName()` is respected over computed name
- **Multi-column indexes**: `Index(x => new { x.A, x.B })` produces correct SurrealQL with two columns
- **`SchemaRoutingTests`**: Already extensive (582 lines) — covers schema-based database routing

---

## Tracking Metadata

| Field | Value |
|-------|-------|
| **Document** | Marten vs Dali API Gap Analysis |
| **Created** | 2026-06-28 |
| **Audit scope** | Marten docs (`docs/marten-llms-full.txt`) vs Dali source (`src/Dali/`) |
| **Audit version** | Dali main branch, commit HEAD |
| **Total gaps identified** | 44 (7 Critical, 20 Major, 17 Minor) |
| **At parity** | 24 areas confirmed implemented |
| **Next review** | TBD — after gaps marked completed |
