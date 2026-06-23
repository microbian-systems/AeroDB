# Dali vs Marten — Feature Comparison

## Overall Status

| Status | Count |
|--------|-------|
| ✅ Done | 19 |
| ⚠️ Partial | 9 |
| ❌ Missing | 42 (16 high-impact) |
| 🟢 Dali Unique | 27 |

---

## Session / Document APIs

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `Query<T>()` LINQ | ✅ Done | `IQuerySession.Query<T>()` → `SurrealQueryProvider` |
| `LoadAsync<T>(id)` | ✅ Done | `IQuerySession.LoadAsync<T>(string id)` |
| `LoadMany<T>(ids...)` | ❌ Missing | — |
| `AdvancedSql.QueryAsync<T>()` | ⚠️ Partial | `RawQueryAsync<T>()` — raw SurrealQL, no multi-doc tuples, no `ROW()` wrapping, no streaming |
| `AdvancedSql.StreamAsync<T>()` (IAsyncEnumerable) | ❌ Missing | Dali has `IAsyncEnumerable` only for LIVE SELECT, not for raw SQL streaming |
| `ToCommand()` / SQL inspection | ❌ Missing | No way to inspect generated SurrealQL before execution |
| `IBatchedQuery` / `CreateBatchQuery()` | ❌ Missing | Single-roundtrip multi-query batching |
| `ICompiledQuery<T>` | ⚠️ Partial | Dali's `CompiledQuery<T>` compiles expression → SurrealQL; Marten's is a declarative interface |
| `BulkInsert` (COPY-based) | ⚠️ Partial | `BulkOperations.BulkInsertAsync<T>()` — uses batched Store+SaveChanges, not optimized SurrealDB IMPORT |
| Document hierarchy / polymorphism | ❌ Missing | No `mt_doc_type` style polymorphic support |
| Raw multi-document tuple queries | ❌ Highest Gap | Marten returns `(DocA, DocB, long)` tuples from single SQL query |

---

## Projections

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `SingleStreamProjection<TDoc,TId>` | ✅ Done | `SingleStreamProjection<T>` — one doc per stream |
| `MultiStreamProjection<TDoc,TId>` | ✅ Done | `MultiStreamProjection<T>` — user-defined grouping across streams |
| `EventProjection` | ❌ Missing | Flat per-event handler with `Project<T>()` / `Transform<T>()` conventions |
| `Create()` / `Apply()` / `ShouldDelete()` conventions | ⚠️ Partial | Dali uses single `ApplyEvents()` override; Marten has source-generated method dispatch on `partial` classes |
| `Evolve()` / `EvolveAsync()` / `DetermineAction()` | ❌ Missing | Marten explicit-code aggregation with `ActionType` (Store, Nothing, SoftDelete, HardDelete) |
| `Snapshot<T>()` (self-aggregating) | ❌ Missing | Register aggregate class directly as a projection |
| `LiveStreamAggregation<T>()` | ❌ Missing | Live-calculated aggregations from raw events |
| `FlatTableProjection` | ❌ Missing | Declarative event → SQL table column mapping |
| `IProjection` custom | ✅ Done | `IProjection` interface — both support custom low-level |
| Inline / Async / Live lifecycle | ⚠️ Partial | `ProjectionLifecycle.Inline` / `.Async` — no `Live` lifecycle |
| Async Daemon (background) | ✅ Done | `AsyncDaemon` — polling-based, simpler than Marten's |
| Async Daemon HealthChecks | ❌ Missing | Marten has `AddMartenAsyncDaemonHealthCheck()` |
| Side effects (`RaiseSideEffects`) | ❌ Missing | No hook to raise events/messages from projection step |
| Event enrichment (IQuerySession in projection) | ❌ Missing | No pre-apply enrichment hook |
| Aggregate caching (LRU) | ❌ Missing | No cache for projected aggregates in the daemon |
| `EfCoreEventProjection<TDbContext>` | ❌ Missing | Write to EF Core alongside Marten in same transaction |
| **IEvent\<T\> metadata envelope** | ❌ Highest Gap | Projections need event metadata (version, timestamp, sequence) — unlocks rich projections |
| **Create/Apply/ShouldDelete source-gen** | ❌ Highest Gap | Idiomatic projection authoring without runtime reflection |
| **Evolve/DetermineAction explicit** | ❌ Highest Gap | Reentrant workflows, soft-delete undo |

---

## Schema

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `DatabaseSchemaName` / `EventsSchemaName` | ❌ Missing | Separate schema for event store tables |
| `schema.For<T>()` → qualified name | ✅ Done | `Schema.For<T>()` returns `DocumentMapping<T>` |
| `schema.ForStreams()` / `schema.ForEvents()` | ❌ Missing | Expose event table names for raw SQL |
| `schema.ForEventProgression()` | ❌ Missing | Projection progress table reference |
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
| Graph traversal | 🟢 Unique | `IGraphQuery<T>` / `GraphQueryBuilder` — `->edge_type->`, In/Out/Both, depth, shortest path |
| Full-text search | 🟢 Unique | `ISearchQuery<T>` / `DaliSearchQuery<T>` — BM25, field weights, `search::score()` |
| Vector search (KNN) | 🟢 Unique | `ISearchQuery<T>.WithVector()`, HNSW/MTREE/DISKANN indexes |
| Hybrid search (RRF fusion) | 🟢 Unique | `ISearchQuery<T>.ExecuteHybridAsync()` — Reciprocal Rank Fusion |
| Geo-spatial queries | 🟢 Unique | `ISpatialQuery<T>` / `DaliSpatialQuery<T>` — NearBy, Within polygon, OrderByDistance |
| Time-series queries | 🟢 Unique | `ITimeSeriesQuery<T>` / `DaliTimeSeriesQuery<T>` — BucketByFloor, BucketByGroup, Downsample |
| Live queries (realtime) | 🟢 Unique | `ILiveQuery<T>` / `WatchTableAsync` / `WatchQueryAsync` — `LIVE SELECT` via WebSocket |
| Record links | 🟢 Unique | `Include<T,TInclude>()`, `IncludeReverse()`, `FilterInclude()` — FK-record resolution |
| Edge/Relation support | 🟢 Unique | `EdgeRecord`, `EdgeMapping<T>`, `RelateAsync()` — `RELATE` / `UNRELATE` |
| Patching API | 🟢 Unique | `PatchExpression`, `ISurrealDbQueryable.PatchAsync()` — field-level updates |
| Machine Learning | 🟢 Unique | `IMlQuery<TInput,TOutput>.ComputeAsync()` — `ml::model<version>()` inference (lowest priority) |

---

## Metadata

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| Document version (`mt_version`) | ✅ Done | `IVersioned` / `VersionAttribute` via `MetadataRegistry` |
| `mt_last_modified` | ❌ Missing | No auto-tracked last-modified |
| `mt_created_at` | ❌ Missing | No auto-tracked created-at |
| `mt_deleted` / `mt_deleted_at` (soft delete) | ✅ Done | `ISoftDeleted` / `SoftDeleteExtensions` — auto-filtered |
| `correlation_id` / `causation_id` | ❌ Missing | No event metadata correlation tracking |
| `last_modified_by` | ❌ Missing | No user-based audit metadata |
| Event metadata on `IEvent<T>` (version, timestamp, sequence) | ❌ Missing | Dali events are plain `object` — no typed envelope |

---

## Events

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| `Append(Guid streamId, object[] events)` | ❌ Missing | Only string identity |
| `Append(string streamId, object[] events)` | ✅ Done | `EventStore.Append(string, IEnumerable<object>)` |
| `Append(streamId, expectedVersion, events)` | ❌ Missing | No explicit expected-version control |
| `AppendOptimistic()` / `AppendExclusive()` | ❌ Missing | No optimistic/exclusive locking variants |
| `StartStream<T>(Guid id, events...)` | ⚠️ Partial | `EventStore.StartStream(streamId, events)` — string only, no stream type |
| `FetchStreamAsync(Guid id)` | ❌ Missing | Only `FetchStream(string streamId)` |
| `FetchStreamAsync(string id)` | ✅ Done | `EventStore.FetchStream(string)` → `IReadOnlyList<object>` |
| **FetchForWriting\<T\>()** | ❌ Highest Gap | Load aggregate + append + save — canonical CQRS pattern |
| `FetchLatest<T>()` | ❌ Missing | Fetch latest aggregate snapshot without loading stream |
| `AggregateStreamAsync<T>()` | ❌ Missing | Live-aggregate from raw events (no projection needed) |
| `IEvent<T>` typed metadata envelope | ❌ Missing | No version/timestamp/sequence on events |
| Version checking on append | ⚠️ Partial | Auto-versioned on storage, no client-side expected-version guard |
| Stream archiving (`ArchiveStream`) | ❌ Missing | No archive API |
| Tombstone events | ❌ Missing | No gap-filling for failed transactions |
| Binary event serialization | ❌ Missing | JSON only |
| Event versioning / upcasters | ❌ Missing | No event type migration |
| `EventAppendMode` (Rich / Quick) | ❌ Missing | Single append path |

---

## Identity

| Feature | Dali Status | Dali API |
|---------|-------------|----------|
| Guid-based stream identity | ❌ Missing | Dali uses string-only event stream IDs |
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
| `IConfigureMarten` composite config | ⚠️ Partial | `IConfigureDali` — similar pattern |
| `IInitialData` seeding | ❌ Missing | No initial data seeding hook |
| `IDocumentSessionListener` / `IChangeListener` | ✅ Done | `IDocumentSessionListener` |
| Multi-host / read replica support | ❌ Missing | Postgres-specific |
| **FetchForWriting\<T\>()** | ❌ Highest Gap | **[duplicate]** |
| **IBatchedQuery** | ❌ Highest Gap | Performance optimization |
| **Multi-doc tuple queries** | ❌ Highest Gap | Efficient complex queries |

---

## Priority Gaps for Implementation

ML stuff goes last. Priority order for the rest:

| # | Feature | Complexity | Why |
|---|---------|-----------|-----|
| 1 | `IEvent<T>` metadata envelope | Medium | Unlocks rich projections (version, timestamp, sequence on every event) |
| 2 | `Create/Apply/ShouldDelete` conventions | Large | Idiomatic projection authoring with source-generated dispatch |
| 3 | `FetchForWriting<T>()` | Medium | Canonical CQRS pattern — load aggregate, append, save in one call |
| 4 | `Evolve`/`DetermineAction` explicit code | Medium | Reentrant workflows, soft-delete undo |
| 5 | Multi-doc tuple queries (`ROW()` / streaming) | Large | Efficient complex queries with single round-trip |
| 6 | `EventProjection` / `Snapshot<T>` | Medium | Per-event handler pattern, self-aggregating |
| 7 | Event metadata columns (correlation, causation) | Small | Audit trail, distributed tracing |
| 8 | Side effects (`RaiseSideEffects`) | Medium | Append events / send messages from projections |
| 9 | `Append(expectedVersion, events)` | Small | Optimistic concurrency for event streams |
| 10 | `AggregateStreamAsync<T>()` | Medium | Live aggregation without a projection class |
| 11 | `IBatchedQuery` | Medium | Performance optimization |
| 12 | Guid stream identity support | Small | Marten default — wide compatibility |
| 13 | Async Daemon health checks | Small | Production ops |
| 14 | Document metadata (last_modified, created_at) | Small | Audit trail |
| 15 | `LoadMany<T>(ids...)` | Small | Batch document load |

---

## File Map

| Path | Purpose |
|------|---------|
| `src/Dali/` | Core Dali library (19 ✅ Done features) |
| `src/Dali/Linq/` | LINQ provider, ExpressionVisitor, query generation |
| `src/Dali/Projections/` | SingleStream/MultiStream projections, IProjection |
| `src/Dali/Events/` | Event store, subscriptions, async daemon |
| `src/Dali/Schema/` | Document mapping, index definition, schema manager |
| `src/Dali/Spatial/` | Geo-spatial queries (🟢 Unique) |
| `src/Dali/TimeSeries/` | Time-series bucketing (🟢 Unique) |
| `src/Dali.ML/` | Machine learning queries (🟢 Unique — lowest priority) |
| `docs/dali-vs-marten-comparison.md` | This file |
