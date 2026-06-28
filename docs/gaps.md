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

## 🟡 Major Gaps

### 6. Event causation (CorrelationId / CausationId / Headers)

**Marten**: Every `IEvent` carries `CorrelationId`, `CausationId`, and `Headers` (dictionary) for tracing event chains across streams. CausationId references the event that triggered this one; Headers carry arbitrary metadata.

**Dali**: `IEvent` / `IEvent<T>` only exposes `StreamId`, `StreamKey`, `Version`, `Sequence`, `Timestamp`, `Data`. No `CorrelationId`, `CausationId`, or `Headers`. The `DaliEnvelopeEvent` wrapper in WolverineFx has these, but only in the Wolverine pipeline.

**Files affected**: `src/Dali/Events/IEvent.cs`, `src/Dali/Events/Event.cs`

---

### 7. `IChangeSet` before/after values

**Marten**: `IChangeSet.Changes()` returns `IChange<T>` where each change has `.Before` and `.After` snapshots of the document, enabling precise change tracking and audit logging.

**Dali**: `IChangeSet` (in `Diagnostics/IChangeSet.cs`) only provides `IReadOnlyList<Operation> Operations` and `IReadOnlyList<(string StreamId, object Event)> AppendedEvents`. No before/after snapshots.

**Files affected**: `src/Dali/Diagnostics/IChangeSet.cs`, `src/Dali/Diagnostics/ChangeSet.cs`

---

### 8. `ForeignKey` constraints

**Marten**: `Schema.For<T>().ForeignKey<TRef>(x => x.RefId)` generates a foreign key constraint in PostgreSQL.

**Dali**: **Not implemented.** SurrealDB has no native FK constraint concept (references are expressed via record IDs and graph edges), but there is no API to declare referential rules at the mapping level.

**Files affected**: `src/Dali/Schema/DocumentMapping.cs`

---

### 9. `ComputedIndex` with custom SQL / index options

**Marten**: `Index(x => x.Number, c => { c.Method = IndexMethod.brin; c.Casing = Casings.Lower; c.SortOrder = SortOrder.Desc; c.Predicate = "(data ->> 'Number')::int > 10"; })` — full control over index method, casing, sort order, and partial index predicates.

**Dali**: `IndexOptions` only supports `IsUnique()` and `WithName()`. No `Method`, `Casing`, `SortOrder`, or `Predicate`. SurrealDB only supports btree, so some options (like brin) are SurrealDB-irrelevant, but no-op stubs are needed for Marten migration compatibility.

**Files affected**: `src/Dali/Schema/IndexDefinition.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 10. `CleanDeletedDocumentsAsync()`

**Marten**: `IDocumentStore.Advanced.CleanDeletedDocumentsAsync()` bulk-purges all soft-deleted records.

**Dali**: **Not implemented.** No way to programmatically purge soft-deleted data. A manual raw SurrealQL query is required.

**Files affected**: None — new feature needed.

---

### 11. `DeletedBefore()` query extension

**Marten**: `session.Query<T>().Where(x => x.DeletedBefore(datetime))` — temporal filter on deletion timestamp in LINQ queries.

**Dali**: **Not implemented.** No LINQ expression support for filtering by `DeletedAt` or `Deleted` status in the query provider.

**Files affected**: `src/Dali/Linq/SurrealExpressionVisitor.cs`

---

### 12. AsyncDaemon persisted watermark

**Marten**: The async daemon persists per-shard sequence numbers to `mt_projection_progress` table, allowing resume-after-restart without full replay.

**Dali**: The `mt_projection_progress` table is defined and created but never written to. The daemon tracks position only in memory. Table read/write plumbing is entirely missing.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 13. `Transform` / JavaScript transformations (PLV8)

**Marten**: Supports event transformations via `Transform` and PLV8 (JavaScript within PostgreSQL).

**Dali**: **Not applicable and not implemented.** SurrealDB has no equivalent of PLV8. No transformation pipeline exists.

**Files affected**: None — not a goal for SurrealDB parity.

---

### 14. `SchemaDiff` / `SchemaPatch` (migration API)

**Marten**: `IDocumentStore.Schema` exposes schema diffing and patch generation for database migrations.

**Dali**: **Not implemented.** No schema comparison, diff, or migration generation API. Schema is applied eagerly via `InitializeAsync()`.

**Files affected**: None — new feature needed.

---

### 15. `SubClass` / type hierarchy on `Schema.For<T>()`

**Marten**: `Schema.For<T>().AddSubClass<TDerived>()` — declarative hierarchy setup per document mapping.

**Dali**: Uses `StoreOptions.HierarchyFor<TBase>()` at the store level instead. Different API shape — works for the same goal but is a migration hurdle.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/DocumentHierarchy.cs`

---

## 🟢 Minor Gaps

| # | Gap | Marten | Dali |
|---|-----|--------|------|
| 16 | **`ISubscriber` / `IChangeListener`** | External event bus subscription model | Not present — uses Wolverine integration or `IDaliSubscription` instead |
| 17 | **`StoreOptions.Serializer()`** | Custom serializer configuration (JSON.NET, STJ) | Hardcoded `System.Text.Json` with snake_case; no user-facing config |
| 18 | **`BulkInsert` on `IDocumentSession`** | `session.BulkInsert<T>(entities)` as interface method | `BulkInsertAsync` is a **static extension** method in `BulkOperations`, not on the interface |
| 19 | **Hard-delete from query** | `session.Query<T>().Where(...).Delete()` | Only entity-based `Delete<T>(entity)` — no query-delete |
| 20 | **Soft-delete field naming** | Shadow columns `mt_deleted`, `mt_deleted_at` | Entity properties via `ISoftDeleted.Deleted` / `DeletedAt` directly |
| 21 | **`IdentityMap` diagnostics** | `IDocumentSession.Database` / `DocumentTracking` tracking modes | `_identityMap` exists on `InternalSessionBase` but no diagnostic/exposure API |
| 22 | **`ICompiledQuery` on `IDocumentStore`** | `store.QueryAsync<TDoc, TOut>(compiled)` | Only on `IQuerySession`, not on `IDocumentStore` |
| 23 | **Projection rebuild progress** | `RebuildAsync` with progress reporting | `RebuildAsync` runs as blocking operation with no progress callback |
| 24 | **Custom projection names** | User-assignable name on `IProjection` | Projections identified by `GetType().Name` only |
| 25 | **Live projection lifecycle** | Async daemon processes all lifecycles | Daemon explicitly filters `Where(p => p.Lifecycle == Async)` — Live projections never processed |
| 26 | **`FlatTableProjection.RebuildAsync`** | Full rebuild support | Returns `Task.CompletedTask` — **stub**, not implemented |

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
| **Total gaps identified** | 26 (5 Critical, 10 Major, 11 Minor) |
| **At parity** | 24 areas confirmed implemented |
| **Next review** | TBD — after gaps marked completed |
