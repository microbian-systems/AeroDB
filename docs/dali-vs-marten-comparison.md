# Dali vs Marten — Feature Comparison

## Overall Status

| Status | Count |
|--------|-------|
| ✅ Done | 62 |
| ⚠️ Partial | 2 |
| ❌ Missing | 2 |
| 🟢 Dali Unique | 27 |

---

## Session / Document APIs

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `Query<T>()` LINQ | ✅ Done | `IQuerySession.Query<T>()` → `SurrealQueryProvider` |
| `LoadAsync<T>(id)` | ✅ Done | `IQuerySession.LoadAsync<T>(string id)` |
| `LoadMany<T>(ids...)` | ✅ Done | `LoadManyExtensions.LoadManyAsync<T>(string[]/RecordId[]/long[])` |
| `AdvancedSql.QueryAsync<T1,T2,...>()` | ✅ Done | `session.AdvancedSql().QueryAsync<T1,T2>()` — 2/3/4-tuple multi-doc queries |
| `AdvancedSql.StreamAsync<T>()` (IAsyncEnumerable) | ✅ Done | `DaliAdvancedSql.StreamAsync<T>()` — streaming raw SQL results |
| `ToCommand()` / SQL inspection | ✅ Done | `ISurrealDbQueryable<T>.ToCommand()` — exposes SurrealQL before execution |
| `IBatchedQuery` / `CreateBatchQuery()` | ❌ Missing | Planned — Marten interface + compiled query integration with SurrealDB multi-statement |
| `ICompiledQuery<T>` | ❌ Missing | Planned — Marten's `ICompiledQuery<TDoc, TOut>` interface pattern + source generator |
| `BulkInsert` (COPY-based) | ⚠️ Partial | `BulkOperations.BulkInsertAsync<T>()` — needs SurrealDB batch INSERT optimization |
| Document hierarchy / polymorphism | ✅ Done | `DocumentHierarchy.AddSubClass<T>()` + `StoreOptions.HierarchyFor<TBase>()` |
| Raw multi-document tuple queries | ✅ Done | `AdvancedSql.QueryAsync<T1,T2,T3,T4>()` — up to 4-tuple |

---

## Projections

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `SingleStreamProjection<TDoc,TId>` | ✅ Done | `SingleStreamProjection<T>` — one doc per stream |
| `MultiStreamProjection<TDoc,TId>` | ✅ Done | `MultiStreamProjection<T>` — user-defined grouping across streams |
| `EventProjection` | ✅ Done | `EventProjection<T>` — per-event handler with `Create`/`Apply`/`ShouldDelete` conventions |
| `Create()` / `Apply()` / `ShouldDelete()` conventions | ✅ Done | Source-generator dispatch on `partial class` + runtime reflection fallback |
| `Evolve()` / `EvolveAsync()` / `DetermineAction()` | ✅ Done | Virtual methods on `InlineProjection<T>` — `ActionType` (Store, Nothing, SoftDelete, HardDelete) |
| `Snapshot<T>()` (self-aggregating) | ✅ Done | `SnapshotProjection<T>` — aggregate applies events to itself via `Apply(EventType)` methods |
| `LiveStreamAggregation<T>()` | ✅ Done | `LiveStreamAggregation.AggregateAsync<T>()` — read-side aggregation without projection |
| `FlatTableProjection` | ✅ Done | `FlatTableProjection<TDoc,TId>` — declarative event→column mapping with `Project`/`Set`/`Delete` |
| `IProjection` custom | ✅ Done | `IProjection` interface — both support custom low-level |
| Inline / Async / Live lifecycle | ⚠️ Partial | `ProjectionLifecycle.Inline` / `.Async` — no `Live` lifecycle |
| Async Daemon (background) | ✅ Done | `AsyncDaemon` — polling-based, simpler than Marten's |
| Async Daemon HealthChecks | ✅ Done | `DaliDaemonHealthCheck` + `AddDaliCheck()` DI extension |
| Side effects (`RaiseSideEffects`) | ✅ Done | `IProjectionContext.RaiseSideEffect()` + re-entrant projection loop (max depth 10) |
| Event enrichment (IQuerySession in projection) | ✅ Done | `IEnrichProjection.EnrichAsync()` — pre-apply enrichment hook |
| Aggregate caching (LRU) | ✅ Done | `AggregateCache` — thread-safe LRU for daemon projection aggregates |
| `EfCoreEventProjection<TDbContext>` | ✅ Done | `EfCoreEventProjection<TDbContext>` — write to EF Core alongside Dali |
| **IEvent\<T\> metadata envelope** | ✅ Done | `IEvent<T>` with Version, Sequence, Timestamp, StreamId, StreamKey, CorrelationId, CausationId |
| **Create/Apply/ShouldDelete source-gen** | ✅ Done | `EventProjection<T>` partial class + `DaliDocumentGenerator` metadata flag |

---

## Schema

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `DatabaseSchemaName` / `EventsSchemaName` | ✅ Done | `EventSourcingOptions.DatabaseSchemaName` / `.EventsSchemaName` |
| `schema.For<T>()` → qualified name | ✅ Done | `Schema.For<T>()` returns `DocumentMapping<T>` |
| `schema.ForStreams()` / `schema.ForEvents()` | ✅ Done | `SchemaOptions.ForStreams<T>()` / `.ForEvents()` / `.EventsTableName` |
| `schema.ForEventProgression()` | ✅ Done | `SchemaOptions.ForEventProgression()` / `.ProjectionProgressTableName` |
| Auto-create schema migration | ✅ Done | `SchemaManager` auto-creates during `InitializeAsync()` |
| SurrealDB `SCHEMAFULL` / `SCHEMALESS` | 🟢 Unique | `DocumentMapping<T>.SetSchemaMode()` |
| Analyzer definitions (`DEFINE ANALYZER`) | 🟢 Unique | `AnalyzerDefinition`, `AnalyzerOptions` |
| SurrealDB Views (`DEFINE TABLE AS SELECT`) | 🟢 Unique | `ViewDefinition`, `ViewSelectBuilder` |
| SurrealDB functions (`DEFINE FUNCTION`) | 🟢 Unique | `FunctionManager`, `FunctionOptions` |
| SurrealDB triggers (`DEFINE EVENT`) | 🟢 Unique | `EventTriggerManager`, `TriggerActionBuilder` |
| Per-schema databases | 🟢 Unique | `DocumentMapping<T>.Schema(string)` routing |

---

## Advanced Querying (Dali Differentiators)

| Feature | Status | Dali API |
|---------|--------|----------|
| Graph traversal | 🟢 Unique | `IGraphQuery<T>` / `GraphQueryBuilder` |
| Full-text search | 🟢 Unique | `ISearchQuery<T>` / `DaliSearchQuery<T>` — BM25, `search::score()` |
| Vector search (KNN) | 🟢 Unique | `ISearchQuery<T>.WithVector()`, HNSW/MTREE/DISKANN |
| Hybrid search (RRF fusion) | 🟢 Unique | `ISearchQuery<T>.ExecuteHybridAsync()` |
| Geo-spatial queries | 🟢 Unique | `ISpatialQuery<T>` / `DaliSpatialQuery<T>` |
| Time-series queries | 🟢 Unique | `ITimeSeriesQuery<T>` / `DaliTimeSeriesQuery<T>` |
| Live queries (realtime) | 🟢 Unique | `ILiveQuery<T>` / `WatchTableAsync` / `WatchQueryAsync` |
| Record links | 🟢 Unique | `Include<T,TInclude>()`, `IncludeReverse()`, `FilterInclude()` |
| Edge/Relation support | 🟢 Unique | `EdgeRecord`, `EdgeMapping<T>`, `RelateAsync()` |
| Patching API | 🟢 Unique | `PatchExpression`, `ISurrealDbQueryable.PatchAsync()` |
| Machine Learning | 🟢 Unique | `IMlQuery<TInput,TOutput>.ComputeAsync()` |

---

## Metadata

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| Document version (`mt_version`) | ✅ Done | `IVersioned` / `VersionAttribute` via `MetadataRegistry` |
| `mt_last_modified` | ✅ Done | `IDocumentMetadata.LastModified` — auto-populated by `DocumentMetadataListener` |
| `mt_created_at` | ✅ Done | `IDocumentMetadata.CreatedAt` — set on first store, preserved thereafter |
| `mt_deleted` / `mt_deleted_at` (soft delete) | ✅ Done | `ISoftDeleted` / `SoftDeleteExtensions` — auto-filtered |
| `correlation_id` / `causation_id` | ✅ Done | `IEvent<T>.CorrelationId` / `.CausationId` — OpenTelemetry auto-population |
| `last_modified_by` | ✅ Done | `IDocumentMetadata.LastModifiedBy` — auto-populated from `session.CurrentUser` |
| Event metadata on `IEvent<T>` (version, timestamp, sequence) | ✅ Done | `Event<T>` sealed record with full metadata envelope |

---

## Events

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `Append(Guid streamId, object[] events)` | ✅ Done | `EventStore.Append(Guid, IEnumerable<object>)` |
| `Append(string streamId, object[] events)` | ✅ Done | `EventStore.Append(string, IEnumerable<object>)` |
| `Append(streamId, expectedVersion, events)` | ✅ Done | `Append(string/Guid, expectedVersion, events)` — throws `ConcurrencyException` |
| `AppendOptimistic()` / `AppendExclusive()` | ✅ Done | `AppendOptimistic(lastKnownVersion)` / `AppendExclusive()` |
| `StartStream<T>(Guid id, events...)` | ✅ Done | `StartStream<T>(string/Guid, events)` |
| `FetchStreamAsync(Guid id)` | ✅ Done | `EventStore.FetchStream(Guid)` → `IReadOnlyList<IEvent>` |
| `FetchStreamAsync(string id)` | ✅ Done | `EventStore.FetchStream(string)` → `IReadOnlyList<IEvent>` |
| **FetchForWriting\<T\>()** | ✅ Done | `FetchForWritingAsync<T>()` — load + replay + handler + store in one call |
| `FetchLatest<T>()` | ✅ Done | `IQuerySession.FetchLatest<T>(string/Guid)` |
| `AggregateStreamAsync<T>()` | ✅ Done | `IEvents.AggregateStreamAsync<T>(string/Guid)` — live replay |
| `IEvent<T>` typed metadata envelope | ✅ Done | `IEvent<T>` / `Event<T>` — full metadata |
| Version checking on append | ✅ Done | `Append(expectedVersion)` + `AppendOptimistic`/`AppendExclusive` |
| Stream archiving (`ArchiveStream`) | ✅ Done | `IEvents.ArchiveStream(string/Guid)` → `mt_archived_streams` |
| Tombstone events | ✅ Done | `IEvents.WriteTombstone(streamId, version)` — `TombstoneEvent` |
| Binary event serialization | ✅ Done | `EventSerializationMode.Binary` — `SerializeToUtf8Bytes` |
| Event versioning / upcasters | ✅ Done | `IEventUpcaster` + `LambdaUpcaster<T>` + `StoreOptions.Events.Upcast<T>()` |
| `EventAppendMode` (Rich / Quick) | ✅ Done | `EventAppendMode.Rich` / `.Quick` |

---

## Identity

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| Guid-based stream identity | ✅ Done | All `IEvents` methods have `Guid` overloads — `Guid.ToString("D")` → string mapping |
| String-based stream identity | ✅ Done | `EventStore.Append(string, ...)` |
| Strongly typed identifiers | ✅ Done | `RecordIdOf<T>` (string, long, int) |
| Int/long document identity | ✅ Done | `RecordIdOf<long>`, `RecordIdOf<int>` |
| Document identity configuration | ✅ Done | `DocumentMapping<T>.Identity<TProp>()` |

---

## Other / Infrastructure

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| Multi-tenant (conjoined / database-per-tenant) | ✅ Done | `TenancyStyle`, `WithTenant()`, `SetTenant()` |
| `AddMarten()` / DI integration | ✅ Done | `AddDali()` / `DaliServiceCollectionExtensions` |
| `IConfigureMarten` composite config | ✅ Done | `IConfigureDali` / `ConfigureDali<T>()` DI extension — sync + async configurators |
| `IInitialData` seeding | ✅ Done | `IInitialData` interface + `StoreOptions.InitialData` list |
| `IDocumentSessionListener` / `IChangeListener` | ✅ Done | `IDocumentSessionListener` |
| Multi-host / read replica support | ✅ Done | `DatabaseEndpoint` + `ReadPreference` — configuration layer |

---

## Remaining Gaps (2 ❌ Missing, 2 ⚠️ Partial)

The original 42 ❌ Missing features are all filled. Two items were discovered to not exist in source (IBatchedQuery, ICompiledQuery). Two ⚠️ Partial items remain:

| Feature | Status | Plan |
|---------|--------|------|
| `ICompiledQuery<TDoc, TOut>` interface pattern | ❌ Missing | Marten-compatible interface + reflection planner + Roslyn source generator (Step 3) |
| `IBatchedQuery` / `CreateBatchQuery()` | ❌ Missing | Batch query with futures pattern + compiled query integration (Step 4) |
| `Live` projection lifecycle | ⚠️ Partial | Add `Live` to `ProjectionLifecycle` enum, wire through `LiveStreamAggregation` (Step 1) |
| `BulkInsert` optimization | ⚠️ Partial | Use SurrealDB batch INSERT INTO syntax instead of per-row Store+SaveChanges (Step 5) |

### Next Implementation Steps

| Step | Feature | Est. Effort |
|------|---------|-------------|
| 1 | Live projection lifecycle — add `Live` to enum, route through `LiveStreamAggregation` | ~2 hrs |
| 2 | Composite configuration — add `IAsyncConfigureDali`, ensure DI auto-resolution | ~1 hr |
| 3 | Compiled queries — interface hierarchy + runtime planner + Roslyn source generator | ~2.5 days |
| 4 | Batch queries — `IBatchedQuery` + `CreateBatchQuery()` with compiled query integration | ~1 day |
| 5 | BulkInsert optimization — SurrealDB batch INSERT syntax | ~1 day |
| 6 | Real SurrealDB integration test suite — `TestHarnessRemote.cs`, Docker compose | ~2 days |
| 7 | Release readiness — XML docs, NuGet metadata, CI config | ~1 day |

---

## Implementation History

| Phase | Feature | Tests Added |
|-------|---------|-------------|
| 1 | `IEvent<T>` + Evolve/DetermineAction | base (824) |
| A | `EventProjection<T>` + `Snapshot<T>` + source-gen | +7 |
| B | Multi-doc tuple queries | +12 |
| C | `AdvancedSql` tuple queries | +8 |
| D | `LoadMany<T>(ids...)` | +5 |
| E | `FetchForWriting<T>()` + `AggregateStreamAsync<T>()` | +5 |
| F | Side effects (`RaiseSideEffects`) | +5 |
| G | `Append(expectedVersion)` + Guid identity | +12 |
| H | Document metadata (last_modified, created_at) | +6 |
| K | Correlation/Causation metadata | +4 |
| I | Async Daemon HealthChecks | +6 |
| J | Binary event serialization | +5 |
| — | FetchLatest<T>() + Schema gaps + last_modified_by + IInitialData | +16 |
| — | StartStream<T> + EventAppendMode + ToCommand | +5 |
| — | AppendOptimistic + LiveStreamAggregation + ArchiveStream + Tombstone + Enrichment + Aggregate cache | +7 |
| — | FlatTableProjection + EfCoreEventProjection + Event versioning + Document hierarchy + Multi-host | +13 |

**Total**: 824 → 920 tests (+96). Last updated: 2026-06-23. Next steps: Steps 1-7 (see above).

## File Map

| Path | Purpose |
|------|---------|
| `src/Dali/` | Core Dali library (**62 ✅ Done** features — 2 ❌ Missing planned) |
| `src/Dali/Linq/` | LINQ provider, ExpressionVisitor, query generation, ToCommand |
| `src/Dali/Projections/` | SingleStream/MultiStream/EventProjection, Snapshot, SideEffects, LiveStreamAggregation, AggregateCache, IEnrichProjection, FlatTableProjection |
| `src/Dali/Events/` | Event store, IEvent<T>, ArchiveStream, TombstoneEvent, AppendOptimistic, AsyncDaemon, IEventUpcaster |
| `src/Dali/AdvancedSql/` | Multi-doc tuple queries, streaming |
| `src/Dali/Batching/` | *(planned)* IBatchedQuery — single-roundtrip multi-query |
| `src/Dali/Compiled/` | *(planned)* ICompiledQuery interfaces + runtime planner + source generator |
| `src/Dali/Schema/` | Document mapping, index definition, schema manager, DocumentHierarchy |
| `src/Dali/Metadata/` | Document metadata auto-tracking |
| `src/Dali/Health/` | ASP.NET Core health check integration |
| `src/Dali/MultiHost/` | DatabaseEndpoint, ReadPreference — multi-endpoint configuration |
| `src/Dali/Concurrency/` | Optimistic concurrency (expectedVersion, stream-level) |
| `src/Dali.EntityFrameworkCore/` | EfCoreEventProjection — EF Core transaction bridge |
| `src/Dali/Spatial/` | Geo-spatial queries (🟢 Unique) |
| `src/Dali/TimeSeries/` | Time-series bucketing (🟢 Unique) |
| `src/Dali.ML/` | Machine learning queries (🟢 Unique — lowest priority) |
| `docs/dali-vs-marten-comparison.md` | This file |
