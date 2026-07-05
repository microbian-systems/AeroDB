# Marten vs Dali — API Gap Analysis

Audit of Marten's API surface (from .docs/design/research/marten-llms-full.txt) and SurrealDB built-in functions (via SurrealDB MCP) against Dali's current implementation (from src/Dali/ and src/Dali.WolverineFx/).

---

## 🔴 Critical Gaps (block Marten migration)

### 1. `FetchForWriting` ✅ RESOLVED (Phase 8)

**Marten**: `session.Events.FetchForWriting<T>(id)` loads the aggregate document, tracks the expected stream version, and lets callers `quest.AppendOne(new Event(...))`. On `SaveChangesAsync`, Marten verifies the stream version matches and throws `EventStreamUnexpectedMaxEventIdException` on conflict.

**Dali**: **RESOLVED.** `IEvents.FetchForWritingAsync<T>(streamId)` returns `FetchForWritingResult<T>` with `ExpectedVersion`, `AppendOne()`, and `AppendMany()`. The caller must manually call `Append(streamId, expectedVersion, events)` — auto-flush in `SaveChangesAsync` is tracked as gap #1a (Phase 9).

**Files affected**: `src/Dali/IEvents.cs`, `src/Dali/Events/EventStore.cs`, `src/Dali/Events/FetchForWritingResult.cs`

---

### 2. `AggregateStream<T>()` on session ✅ RESOLVED (Phase 8)

**Marten**: `session.Events.AggregateStreamAsync<T>(id, version?)` — single call to load all events for a stream and fold them into the aggregate type via convention-based `Apply`/`Create` methods.

**Dali**: **RESOLVED.** `IEvents.AggregateStreamAsync<T>(id)` delegates to `LiveStreamAggregation.AggregateEvents<T>()`, returns `default` for empty streams.

**Files affected**: `src/Dali/Events/LiveStreamAggregation.cs`, `src/Dali/Events/IEvents.cs`, `src/Dali/Events/EventStore.cs`

---

### 3. Projection progress persistence ✅ RESOLVED (Phase 7b)

**Marten**: The async daemon persists projection high-water marks (sequence numbers) to `mt_projection_progress` table. On restart, the daemon resumes from the last persisted position.

**Dali**: **RESOLVED in Phase 7b.** `AsyncDaemon` now loads `_highWaterSequence` from `mt_projection_progress` on start and persists progress after each poll cycle. The table schema is created during `InitializeAsync`.

**Known limitation (gap #17b)**: Per-projection sequence filtering for performance — projections may re-process events already handled. Tracked as a Phase 10 item.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Projections/ProjectionProgress.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 4. `IgnoreIndex` / `IgnoreIdentifier` ✅ RESOLVED (Phase 9)

**Marten**: `Schema.For<T>().IgnoreIndex("index_name")` excludes a database index from schema delta computation.

**Dali**: **RESOLVED.** `DocumentMapping.IgnoreIndex(string)` with `IgnoredIndexes` property. SchemaManager skips index creation for ignored indexes.

**Files affected**: `src/Dali/Schema/DocumentMapping.cs`

---

### 5. `MetadataColumn` / `MetadataConfig` ✅ RESOLVED (Phase 9)

**Marten**: `Schema.For<T>().Metadata(...)` — fluent API for metadata column mapping.

**Dali**: **RESOLVED.** `MetadataConfig` class with `EnableCorrelationId()`, `EnableCausationId()`, `EnableHeaders()`, `EnableAll()`. Default: `HeadersEnabled = true`, others disabled. Mounted on `EventSourcingOptions`.

**Files affected**: `src/Dali/Events/MetadataConfig.cs`, `src/Dali/StoreOptions.cs`

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

### 8. `DEFINE ACCESS` — database-level authentication ✅ RESOLVED (Phase 10)

**SurrealDB**: `DEFINE ACCESS ... ON DATABASE TYPE RECORD SIGNUP (...) SIGNIN (...) DURATION FOR TOKEN 24h` (v3.x renaming of DEFINE LOGIN).

**Dali**: **RESOLVED.** `AccessDefinition` class with `EnsureAccessesAsync` in SchemaManager. Configured via `StoreOptions.Schema.Accesses`.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/SchemaManager.cs`, `src/Dali/DocumentStore.cs`

---

### 9. `DEFINE TOKEN` — JWT authentication tokens ✅ RESOLVED (Phase 10)

**SurrealDB**: `DEFINE TOKEN name ON DATABASE TYPE HS256 VALUE "secret"` — JWT tokens.

**Dali**: **RESOLVED.** `TokenDefinition` class with `EnsureTokensAsync` in SchemaManager. Configured via `StoreOptions.Schema.Tokens`.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 10. `DEFINE SCOPE` — authentication scopes ✅ RESOLVED (Phase 10)

**SurrealDB**: `DEFINE SCOPE name SESSION 24h SIGNUP (...) SIGNIN (...)` — full auth flow.

**Dali**: **RESOLVED.** `ScopeDefinition` class with `EnsureScopesAsync` in SchemaManager. Configured via `StoreOptions.Schema.Scopes`.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/SchemaManager.cs`

**Files affected**: `src/Dali/Schema/SchemaManager.cs`

---

## 🟡 Major Gaps (#11-#69)

### 11. Event causation (Headers) ✅ RESOLVED (Phase 8)

**Marten**: Every `IEvent` carries `CorrelationId`, `CausationId`, and `Headers` (dictionary) for tracing event chains across streams.

**Dali**: **RESOLVED.** `IEvent.Headers` (Dictionary&lt;string, string&gt;?) exposed on both `IEvent` and `IEvent<T>`. `Append` accepts optional `headers` parameter. Headers serialized as `headers_json` column in `mt_events`. `CorrelationId`/`CausationId` not yet exposed as separate properties (deferred to Phase 11).

**Files affected**: `src/Dali/Events/IEvent.cs`, `src/Dali/Events/Event.cs`, `src/Dali/Events/EventStore.cs`

---

### 12. `IChangeSet` before/after values

**Marten**: `IChangeSet.Changes()` returns `IChange<T>` where each change has `.Before` and `.After` snapshots of the document, enabling precise change tracking and audit logging.

**Dali**: `IChangeSet` (in `Diagnostics/IChangeSet.cs`) only provides `IReadOnlyList<Operation> Operations` and `IReadOnlyList<(string StreamId, object Event)> AppendedEvents`. No before/after snapshots.

**Files affected**: `src/Dali/Diagnostics/IChangeSet.cs`, `src/Dali/Diagnostics/ChangeSet.cs`

---

### 13. `ForeignKey` constraints

**Marten**: `Schema.For<T>().ForeignKey<TRef>(x => x.RefId)` generates a foreign key constraint in PostgreSQL.

**Dali**: **Not implemented.** SurrealDB has no native FK constraint concept (references are expressed via record IDs and graph edges), but there is no API to declare referential rules at the mapping level.

**Files affected**: `src/Dali/Schema/DocumentMapping.cs`

---

### 14. `ComputedIndex` with custom SQL / index options

**Marten**: `Index(x => x.Number, c => { c.Method = IndexMethod.brin; c.Casing = Casings.Lower; c.SortOrder = SortOrder.Desc; c.Predicate = "(data ->> 'Number')::int > 10"; })` — full control over index method, casing, sort order, and partial index predicates.

**Dali**: `IndexOptions` only supports `IsUnique()` and `WithName()`. No `Method`, `Casing`, `SortOrder`, or `Predicate`. SurrealDB only supports btree, so some options (like brin) are SurrealDB-irrelevant, but no-op stubs are needed for Marten migration compatibility.

**Files affected**: `src/Dali/Schema/IndexDefinition.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 15. `CleanDeletedDocumentsAsync()`

**Marten**: `IDocumentStore.Advanced.CleanDeletedDocumentsAsync()` bulk-purges all soft-deleted records.

**Dali**: **Not implemented.** No way to programmatically purge soft-deleted data. A manual raw SurrealQL query is required.

**Files affected**: None — new feature needed.

---

### 16. `DeletedBefore()` query extension

**Marten**: `session.Query<T>().Where(x => x.DeletedBefore(datetime))` — temporal filter on deletion timestamp in LINQ queries.

**Dali**: **Not implemented.** No LINQ expression support for filtering by `DeletedAt` or `Deleted` status in the query provider.

**Files affected**: `src/Dali/Linq/SurrealExpressionVisitor.cs`

---

### 17. AsyncDaemon persisted watermark

**Marten**: The async daemon persists per-shard sequence numbers to `mt_projection_progress` table, allowing resume-after-restart without full replay.

**Dali**: The `mt_projection_progress` table is defined and created but never written to. The daemon tracks position only in memory. Table read/write plumbing is entirely missing.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Schema/SchemaManager.cs`

---

### 18. `Transform` / JavaScript transformations (PLV8)

**Marten**: Supports event transformations via `Transform` and PLV8 (JavaScript within PostgreSQL).

**Dali**: **Deferred.** SurrealDB's experimental `DEFINE FUNCTION ... LANGUAGE JAVASCRIPT` could serve as a future equivalent. Not implemented — deferred to v1.1+.

**Files affected**: None — not a goal for SurrealDB parity.

---

### 19. `SchemaDiff` / `SchemaPatch` (migration API)

**Marten**: `IDocumentStore.Schema` exposes schema diffing and patch generation for database migrations.

**Dali**: **Not implemented.** No schema comparison, diff, or migration generation API. Schema is applied eagerly via `InitializeAsync()`.

**Files affected**: None — new feature needed.

---

### 20. Session types / identity map / dirty tracking

**Marten**: Three session types: `LightweightSession` (no tracking), `DirtyTrackedSession` (identity map + auto-diff on save), `QuerySession` (read-only). `SessionOptions` class configures `DocumentTracking` mode (None / IdentityOnly / DirtyTracking), isolation level, and listeners per session. `OpenSession(SessionOptions)` is the unified factory.

**Dali**: Three factory methods exist (`LightweightSessionAsync`, `DocumentSessionAsync`, `QuerySessionAsync`) but `DocumentSessionAsync` is identical to `LightweightSessionAsync` — the `_isDirtyTracking` field is never read (dead code). `IdentityMap` dictionary exists on `InternalSessionBase` but is never populated by `LoadAsync<T>`. No `SessionOptions` class, no `DocumentTracking` enum, no auto-dirty-detection on `SaveChangesAsync`. `OpenSessionAsync(opts)` factory missing.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentSession.cs`, `src/Dali/InternalSessionBase.cs`, `src/Dali/StoreOptions.cs`

---

### 21. `SubClass` / type hierarchy on `Schema.For<T>()`

**Marten**: `Schema.For<T>().AddSubClass<TDerived>()` — declarative hierarchy setup per document mapping.

**Dali**: Uses `StoreOptions.HierarchyFor<TBase>()` at the store level instead. Different API shape — works for the same goal but is a migration hurdle.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/DocumentHierarchy.cs`

---

### 22. `Policies` system / `IDocumentPolicy`

**Marten**: `StoreOptions.Policies.ForAllDocuments(Action<DocumentMapping>)`, `Policies.ForDocumentsOfType<T>(Action<DocumentMapping>)`, `Policies.UseOptimisticConcurrency()`, `Policies.SetAllProperties()`, custom `IDocumentPolicy` plugin interface.

**Dali**: Only `StoreOptions.UseOptimisticConcurrency` as a `bool`. No `IDocumentPolicy`, no `ForAllDocuments()`, no `SetAllProperties()`, no plugin system.

**Files affected**: `src/Dali/StoreOptions.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 23. Session-level listeners

**Marten**: `SessionOptions.Listeners` — listeners attached per-session via `store.OpenSession(new SessionOptions { Listeners = { new MyListener() } })`.

**Dali**: Only global `StoreOptions.Listeners`. No per-session listener injection.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentStore.cs`, `src/Dali/StoreOptions.cs`

---

### 24. `IBatchedQuery` raw SQL

**Marten**: `batch.Query<T>(sql, parameters)` — batch executes raw SQL strings alongside compiled queries.

**Dali**: `IBatchedQuery` only supports `Query<TDoc, TOut>(ICompiledQuery)`. No raw SQL overload.

**Files affected**: `src/Dali/IBatchedQuery.cs`, `src/Dali/BatchedQuery.cs`

---

### 25. `IDocumentOperations` interface ✅ RESOLVED (Phase 8)

**Marten**: `IDocumentOperations` is the write-side parent of `IDocumentSession`, used as the parameter type in projection methods.

**Dali**: **RESOLVED.** `IDocumentOperations` interface extracted with `QueryAsync<T>`, `Store<T>(T)`, `Store<T>(string,T)`, `Delete<T>`, `DeleteWhere<T>`. `IDocumentSession` inherits from `IDocumentOperations`. Missing `LoadAsync<T>(id)` tracked as gap #25a (Phase 9).

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/IDocumentOperations.cs`, `src/Dali/DocumentSession.cs`

---

### 26. Projection sharding ✅ RESOLVED (Phase 18)

**Marten**: Async daemon partitions work per-projection across multiple shards (`IProjectionSource`, `IProjectionShard`, `ShardName`). Each shard has independent progress tracking.

**Dali**: **RESOLVED.** `AsyncDaemon` refactored from single-threaded loop to parallel shard workers. Each projection runs in its own `ProjectionShard` with independent health, watermark tracking, and lifecycle. `StartAgentAsync`/`StopAgentAsync` provide per-shard control. `Shards` property exposes the active shards.

**Files affected**: `src/Dali/Projections/AsyncDaemon.cs`, `src/Dali/Projections/ProjectionShard.cs`

---

### 27. `CustomGrouping` / `IAggregateGrouper<TId>` ✅ RESOLVED (Phase 18)

**Marten**: `MultiStreamProjection.CustomGrouping(IAggregateGrouper<TId>)`, `CustomGrouping(Func<IQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task>)`, and `IEventSlicer` for custom event-to-aggregate grouping.

**Dali**: **RESOLVED.** `IAggregateGrouper<TId>` interface, `IEventGrouping<TId>` interface, and `EventGrouping<TId>` implementation added. `MultiStreamProjection<T>` now has `CustomGrouping<TId>(IAggregateGrouper<TId>)` and `CustomGrouping(Func<...>)` overloads. `HasCustomGrouper` and `GroupEventsAsync` exposed for pipeline integration.

**Files affected**: `src/Dali/Projections/IAggregateGrouper.cs`, `src/Dali/Projections/MultiStreamProjection.cs`

---

### 28. Composite projections ✅ RESOLVED (Phase 18)

**Marten**: `opts.Projections.CompositeProjectionFor("Name", x => x.Add<A>().Add<B>().Add<C>())` — one projection that composites multiple sub-projections.

**Dali**: **RESOLVED.** `CompositeProjection` class with `Add<T>(T)` and `Life(ProjectionLifecycle)` fluent API. Registered via `StoreOptions.ProjectionBuild.CompositeProjectionFor("Name", cfg)`. Sub-projections are automatically registered alongside the composite.

**Files affected**: `src/Dali/Projections/CompositeProjection.cs`, `src/Dali/StoreOptions.cs`, `src/Dali/DocumentStore.cs`

---

### 29. `SessionOptions` class

**Marten**: Rich `SessionOptions` with `Tracking` (DocumentTracking), `Timeout`, `Listeners[]`, `IsolationLevel`, `TenantId`, `Connection`, and static factory methods: `ForConnectionString()`, `ForTransaction()`, `ForCurrentTransaction()`.

**Dali**: No `SessionOptions` class. Session factory methods take only `CancellationToken`.

**Files affected**: `src/Dali/IDocumentStore.cs`, `src/Dali/DocumentStore.cs`

---

### 30. `IDocumentStore.BulkInsertAsync` + `BulkInsertMode`

**Marten**: `store.BulkInsertAsync<T>(entities, mode)` at store level. `BulkInsertMode` enum: `InsertsOnly`, `IgnoreDuplicates`, `OverwriteExisting`, `OverwriteIfVersionMatches`. Tenant-aware overload.

**Dali**: Only `BulkInsertAsync<T>()` as static extension method on `IDocumentSession`. No `BulkInsertMode` enum, no store-level API, no tenant overload.

**Files affected**: `src/Dali/BulkOperations.cs`, `src/Dali/IDocumentStore.cs`

---

### 31. Missing `string::` function mappings (20+ functions)

**SurrealDB**: `string::length()`, `string::trim()`, `string::lowercase()`, `string::uppercase()`, `string::concat()`, `string::repeat()`, `string::replace()`, `string::reverse()`, `string::slice()`, `string::split()`, `string::similarity(sim, a, b)`, `string::distance(a, b)`, `string::is_alphanum()`, `string::is_alpha()`, `string::is_ascii()`, `string::is_lowercase()`, `string::is_uppercase()`, `string::is_numeric()`, `string::is_email()`, `string::is_url()`, `string::is_datetime()`, `string::is_uuid()`.

**Dali**: ❌ Only `string::contains()`, `string::starts_with()`, `string::ends_with()` are mapped in `ExpressionVisitor`. The remaining 20+ throw `NotSupportedException`.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 32. Missing `math::` function mappings (15+ functions)

**SurrealDB**: `math::abs()`, `math::ceil()`, `math::floor()`, `math::round()`, `math::fixed()`, `math::median()`, `math::product()`, `math::pow()`, `math::sqrt()`, `math::log()`, `math::exp()`, `math::mod()`, `math::sign()`, `math::sin()`, `math::cos()`, `math::tan()`, `math::radians()`, `math::degrees()`.

**Dali**: ❌ Only `math::sum()`, `math::min()`, `math::max()`, `math::mean()` are mapped. The remaining 14+ throw `NotSupportedException`.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 33. Missing `crypto::` function mappings (14 functions)

**SurrealDB**: `crypto::argon2::generate()`, `crypto::argon2::compare()`, `crypto::bcrypt::generate()`, `crypto::bcrypt::compare()`, `crypto::pbkdf2::generate()`, `crypto::pbkdf2::compare()`, `crypto::scrypt::generate()`, `crypto::scrypt::compare()`, `crypto::md5()`, `crypto::sha1()`, `crypto::sha256()`, `crypto::sha512()`, `crypto::generate_uuid_v4()`, `crypto::generate_uuid_v7()`.

**Dali**: ❌ None mapped. Critical for password hashing and auth flows.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 34. Missing `session::` and `meta::` function mappings (11 functions)

**SurrealDB**: `session::id()`, `session::origin()`, `session::db()`, `session::ip()`, `session::ns()`, `session::sc()`, `session::tk()`, `session::user()`, `meta::id()`, `meta::table()`, `meta::tb()`.

**Dali**: ❌ None mapped.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 35. Missing `array::` function mappings (15+ functions)

**SurrealDB**: `array::add()`, `array::append()`, `array::prepend()`, `array::remove()`, `array::sort()`, `array::reverse()`, `array::distinct()`, `array::union()`, `array::intersect()`, `array::difference()`, `array::contains()`, `array::complement()`, `array::flatten()`, `array::group()`, `array::map()`, `array::filter()`, `array::first()`, `array::last()`, `array::len()`.

**Dali**: ❌ Only `array::insert()` and `array::find_index()` are used internally via `SetOperation`. None exposed in LINQ.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 36. Missing `object::`, `bytes::`, `duration::`, `json::`, `type::`, `is::` function mappings

**SurrealDB**: `object::entries()`, `object::from_entries()`, `object::keys()`, `object::values()`, `object::len()`, `object::merge()`, `object::omit()`, `object::pick()`, `object::set()`, `object::sets()` — plus all functions in `bytes::`, `duration::`, `json::` categories. `type::is_array()`, `type::is_bool()`, `type::is_bytes()`, `type::is_datetime()`, `type::is_float()`, `type::is_int()`, `type::is_number()`, `type::is_object()`, `type::is_record()`, `type::is_string()`, `type::is_table()`, `type::field()`, `type::string()`, `type::int()`, `type::float()`, `type::bool()`, `type::datetime()`, `type::decimal()`, `type::duration()`. `is::array()`, `is::bool()`, `is::bytes()`, `is::float()`, `is::int()`, `is::number()`, `is::object()`, `is::record()`, `is::string()`, `is::table()`.

**Dali**: ❌ None mapped.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 37. Missing `geo::` and `vector::` function mappings (10+ functions)

**SurrealDB**: `geo::INTERSECTS()`, `geo::CENTROID()`, `geo::IS_WITHIN()`, `geo::ANGLE()`, `geo::AZIMUTH()`, `geo::LENGTH()`, `geo::PERIMETER()`. `vector::distance::euclidean()`, `vector::distance::manhattan()`, `vector::distance::minkowski()`, `vector::distance::chebyshev()`, `vector::distance::hamming()`, `vector::dimension()`.

**Dali**: ⚠️ Partial. `geo::DISTANCE()`, `geo::BEARING()`, `geo::AREA()`, INSIDE, CONTAINS are mapped. `vector::distance::knn()`, `vector::similarity::cosine()` are mapped. Remaining 13 not mapped.

**Files affected**: `src/Dali/Linq/GeoExpressionHandler.cs`, `src/Dali/Linq/ExpressionVisitor.cs`

---

### 38. Missing `search::` function mappings (3 functions)

**SurrealDB**: `search::highlight()`, `search::offsets()`, `search::analyze()`.

**Dali**: ⚠️ Partial. `search::score()` and `search::rrf()` are mapped. Remaining 3 not mapped.

**Files affected**: `src/Dali/Linq/SearchQuery.cs`

---

### 39. SurrealQL scripting and procedural features

**SurrealDB**: `FOR $i IN [1,2,3] { ... }` loops, `IF condition THEN ... ELSE ... END` conditionals, `THROW "error"` for error handling, `RETURN BEFORE / AFTER / DIFF / NONE` modifiers on UPDATE/DELETE, `WAIT` / `SLEEP` for delays.

**Dali**: ⚠️ Partial. `BEGIN`/`COMMIT` transactions are used internally. `FOR`, `IF/ELSE`, `THROW`, `RETURN` modifiers, `WAIT`/`SLEEP` are not exposed.

**Files affected**: `src/Dali/DocumentSession.cs`, `src/Dali/Linq/SurrealQueryProvider.cs`

---

### 40. Missing query operators: CONTAINSALL/CONTAINSANY/CONTAINSNONE/INTERSECTS/HAVING/ANALYZE

**SurrealDB**: `CONTAINSALL`, `CONTAINSANY`, `CONTAINSNONE` (array comparison), `INTERSECTS` (geo), `HAVING` (GROUP BY post-filter), `ANALYZE` (query plan inspection).

**Dali**: ❌ Only `CONTAINS` and `INSIDE` are supported. No variants, no `HAVING`, no `ANALYZE`.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`, `src/Dali/Linq/SurrealQueryProvider.cs`

---

### 41. Schema field-level definitions (DEFAULT, ASSERT, PERMISSIONS, record<T>)

**SurrealDB**: `DEFINE FIELD name ON TABLE person TYPE string DEFAULT "unknown"`, `DEFINE FIELD age ON TABLE person TYPE int ASSERT $value > 0`, `DEFINE FIELD salary ON TABLE person TYPE number PERMISSIONS FOR select WHERE $auth.id = NONE`, `DEFINE FIELD manager ON TABLE person TYPE record(employee)`.

**Dali**: ❌ `DEFINE FIELD` is used only for internal event tables. User document mappings have no support for DEFAULT, ASSERT (validation), PERMISSIONS (RLS), or `record<T>` references.

**Files affected**: `src/Dali/Schema/SchemaManager.cs`, `src/Dali/Schema/DocumentMapping.cs`

---

### 42. Missing `rand::`, `uuid()`, `http::`, `parse::`, `series::` functions

**SurrealDB**: `rand::uuid()`, `rand::ulid()`, `rand::int()`, `rand::float()`, `rand::string()`, `rand::enum()`, `rand::boolean()`, `rand::guid()`, `uuid()`, `http::get()`, `http::post()`, `http::put()`, `http::patch()`, `http::delete()`, `parse::email()`, `parse::phone()`, `series::*` (window functions), `count()` with `HAVING`.

**Dali**: ❌ None mapped.

**Files affected**: `src/Dali/Linq/ExpressionVisitor.cs`

---

### 65. `SingleStreamProjection<TDoc, TId>` — 2 type parameters

**Marten**: `public abstract class SingleStreamProjection<TDoc, TId>` — typed stream identity (Guid, string, int, etc.). Convention-based `static Create(EventType)` / `Apply(EventType, TDoc)` dispatch.

**AeroDB**: Only has `SingleStreamProjection<T> where T : class` (1 type parameter). No typed identity, no static Create/Apply dispatch in this class.

**Impact**: DocSamples `QuestProjection : SingleStreamProjection<Quest, Guid>` cannot compile. Also affects any sample that needs typed stream identity.

**Resolution**: Add new `SingleStreamProjection<TDoc, TId> : InlineProjection<TDoc>` with dual-mode dispatch (static Marten conventions + instance AeroDB conventions). Typed `GetDocumentIdTyped()` and reflection-based Create/Apply/ShouldDelete dispatch. Keep existing 1-param class for backward compat.

**Files affected**: `src/AeroDB/Projections/SingleStreamProjection.cs` (new file for 2-param variant)

---

### 66. Non-generic `EventProjection` base class

**Marten**: `public abstract class EventProjection` — non-generic, subclasses override `ApplyAsync(IDocumentOperations operations, IEvent e, CancellationToken)`. No aggregate document type.

**AeroDB**: Only has `EventProjection<T> where T : class` (generic, requires aggregate document type).

**Impact**: DocSamples `MySpecialProjection : EventProjection` cannot compile. Also affects any custom projection that wants direct `IDocumentOperations` access without a typed aggregate.

**Resolution**: Add non-generic `EventProjection : IProjection` with virtual `ApplyAsync(IDocumentOperations, IEvent, CancellationToken)`. Its `IProjection.ApplyAsync(IProjectionContext, CancellationToken)` implementation iterates `context.TypedEvents` and calls the virtual method per event. Existing `EventProjection<T>` unchanged.

**Files affected**: New file `src/AeroDB/Projections/EventProjectionBase.cs`

---

### 67. Missing `ProjectionCollection` fluent API (Add<T>/Snapshot/LiveStreamAggregation)

**Marten**: `StoreOptions.Projections` is a rich `ProjectionOptions` class with fluent methods:
- `Add<T>(ProjectionLifecycle)` — register typed projection with lifecycle
- `Add(instance, ProjectionLifecycle)` — register projection instance with lifecycle
- `Snapshot<T>(SnapshotLifecycle)` — self-aggregating snapshot
- `LiveStreamAggregation<T>()` — read-side only aggregation

**AeroDB**: `StoreOptions.Projections` is `List<IProjection>` — raw list, no fluent methods.

**Impact**: DocSamples `RegisteringProjections.cs` uses `opts.Projections.Add<MySpecialProjection>(ProjectionLifecycle.Live)`, `opts.Projections.Snapshot<QuestParty>(...)`, `opts.Projections.LiveStreamAggregation<QuestParty>()`. None compile against AeroDB.

**Resolution**: Create `ProjectionCollection : IList<IProjection>` class with all 4 fluent methods. Change `StoreOptions.Projections` type to `ProjectionCollection`. Add `internal set;` to `IProjection.Lifecycle` so lifecycle can be assigned from the collection methods.

**Files affected**: New `src/AeroDB/Projections/ProjectionCollection.cs`, modify `src/AeroDB/Projections/IProjection.cs`, modify `src/AeroDB/StoreOptions.cs`

---

## 🟢 Minor Gaps

| # | Gap | Marten | Dali |
|---|-----|--------|------|
| 43 | **`ISubscriber` / `IChangeListener`** | External event bus subscription model | Not present — uses Wolverine integration or `IDaliSubscription` instead |
| 44 | **`StoreOptions.Serializer()`** | Custom serializer configuration (JSON.NET, STJ) | Hardcoded `System.Text.Json` with snake_case; no user-facing config |
| 45 | **`BulkInsert` on `IDocumentSession`** | `session.BulkInsert<T>(entities)` as interface method | `BulkInsertAsync` is a **static extension** method in `BulkOperations`, not on the interface |
| 46 | **Hard-delete from query** | `session.Query<T>().Where(...).Delete()` | Only entity-based `Delete<T>(entity)` — no query-delete |
| 47 | **Soft-delete field naming** | Shadow columns `mt_deleted`, `mt_deleted_at` | ✅ Updated XML doc to explain the inline-property approach vs Marten shadow columns |
| 48 | **`IdentityMap` diagnostics** | `IDocumentSession.Database` / `DocumentTracking` tracking modes | ✅ Implemented in Phase 13 — `IdentityMapCount` and `IdentityMapKeys` exposed on `InternalSessionBase` |
| 49 | **`ICompiledQuery` on `IDocumentStore`** | `store.QueryAsync<TDoc, TOut>(compiled)` | Only on `IQuerySession`, not on `IDocumentStore` |
| 50 | **Projection rebuild progress** | `RebuildAsync` with progress reporting | `RebuildAsync` runs as blocking operation with no progress callback |
| 51 | **Custom projection names** | User-assignable name on `IProjection` | Projections identified by `GetType().Name` only |
| 52 | **Live projection lifecycle** | Async daemon processes all lifecycles | Daemon explicitly filters `Where(p => p.Lifecycle == Async)` — Live projections never processed |
| 53 | **`FlatTableProjection.RebuildAsync`** | Full rebuild support | ✅ Implemented — replays events grouped by stream using EventRow deserialization |
| 54 | **`BulkInsertEventsAsync`** | `store.BulkInsertEventsAsync(streams, batchSize)` — bulk-insert entire event streams at store level | ✅ Implemented on `IEvents`/`EventStore` with batched SurrealQL INSERT statements |
| 55 | **Per-session logger swap** | `IMartenSessionLogger` swappable per-session; `session.Logger = new RecordingLogger()` | ✅ Implemented — `IDocumentSession.Logger` property with `_loggerOverride` fallback in `DocumentSession` |
| 56 | **`RequestCount` on sessions** | `session.RequestCount` — number of DB commands issued by that session | ✅ Implemented in Phase 13 — `InternalSessionBase.RequestCount` increments on each DB operation |
| 57 | **Event data masking (GDPR)** | `store.Advanced.ApplyEventDataMasking(Func<IEvent, bool>)` with per-event-type redaction | ✅ Implemented in Phase 17 — `EventSourcingOptions.DataMaskingPredicate` + nulled storage in `EventStore.Append` |
| 58 | **`IChangeListener`** | Async daemon pipeline listener with `BeforeCommitAsync`/`AfterCommitAsync` | ✅ Implemented — `IChangeListener` interface + `StoreOptions.ChangeListeners` + wired in `AsyncDaemon.RunAsync` |
| 59 | **`IEventSlice<T>` / enrichment** | `IEventSlice<T>` with `Aggregate`, `Id`, `Events`; `IProjectionEnrichment` hooks | ✅ Implemented in Phase 17 — `IEventSlice<T>` + `EventSlice<T>` + default `EnrichAsync` on `IProjection` |
| 60 | **CONTAINSALL query operator** | `WHERE tags CONTAINSALL ["a","b"]` | ✅ Implemented — `SurrealArrayFunctions.ContainsAll` + extension method + `array::contains_all` translation |
| 61 | **CONTAINSANY query operator** | `WHERE tags CONTAINSANY ["a","b"]` | ✅ Implemented — `SurrealArrayFunctions.ContainsAny` + extension method + `array::contains_any` translation |
| 62 | **CONTAINSNONE query operator** | `WHERE tags CONTAINSNONE ["a","b"]` | ✅ Implemented — `SurrealArrayFunctions.ContainsNone` + extension method + `array::contains_none` translation |
| 63 | **INTERSECTS geo operator** | `WHERE geo INTERSECTS ...` | ✅ Implemented in Phase 17 — `GeoExpressionHandler` now translates `Intersects` → `INTERSECTS` |
| 64 | **`series::*` window functions** | `series::*` for time-series window operations | ✅ Implemented in Phase 17 — `SurrealSeriesFunctions` marker class + `SeriesExpressionHandler` with 11 functions |

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
| **Document** | AeroDB Gap Analysis (Marten → AeroDB API parity) |
| **Created** | 2026-06-28 |
| **Last updated** | 2026-07-04 |
| **Audit source** | Marten `marten/` submodule samples vs AeroDB `src/AeroDB/` |
| **Project** | AeroDB (formerly Dali) |
| **Total gaps identified** | 69 (10 Critical, 35 Major, 22 Minor) |
| **Gaps resolved** | 62 |
| **Critical remaining** | **0** |
| **Major remaining** | 3 (new from Marten sample port: #65-67) |
| **Completion** | 90% (62/69). 5 deferred. |
| **Milestone** | **AeroDB v1.0 — Marten sample port gaps pending (#65-67)** |
| **At parity** | 24 areas confirmed implemented |
| **Next review** | After #65-67 resolved — when DocSamples compiles |

---

## Implementation Roadmap

Council-recommended phase ordering (dependency-graph based):

### P1: Plumbing (Week 1)
| Gap | Deliverable |
|-----|-------------|
| #25 | Extract `IDocumentOperations` interface from `IDocumentSession` |
| #11 | Add `CorrelationId`, `CausationId`, `Headers` to `IEvent` |
| #12 | Capture before/after snapshots in `IChangeSet` |
| #18, #55, #56 | Per-session listeners, logger swap, `RequestCount` |

### P2: Event Core (Week 2)
| Gap | Deliverable |
|-----|-------------|
| #1 | `FetchForWriting` + aggregate version-tracking |
| #2 | `AggregateStream<T>()` on `IEvents` |
| #17 | `mt_projection_progress` read/write plumbing |
| #19 | `IBatchedQuery` raw SQL overloads |

### P3: Schema Surface (Week 3)
| Gap | Deliverable |
|-----|-------------|
| #4 | `IgnoreIndex` / `IgnoreIdentifier` |
| #5 | `MetadataConfig` fluent API |
| #13 | `ForeignKey` stubs |
| #14 | `ComputedIndex` options (Method, Casing, SortOrder, Predicate) |
| #21 | `SubClass` on `Schema.For<T>()` |
| #41 | `DEFINE FIELD` with DEFAULT, ASSERT, PERMISSIONS, record\<T\> |

### P4: Projection v2 (Weeks 4-5)
| Gap | Deliverable |
|-----|-------------|
| #25 | Retrofit `IProjection` to use `IDocumentOperations` |
| #26 | `IProjectionSource` / `IProjectionShard` — sharded daemon |
| #27 | `IAggregateGrouper<TId>` / `IEventSlicer` |
| #28 | `CompositeProjection` |
| #43 | `IEventSlice<T>` + enrichment pipeline |
| ~~#44, #50~~ | ~~Rebuild progress, custom projection names~~ |

### P5: Function Mappings (Weeks 5-7)
| Gap | Strategy | Functions |
|-----|----------|-----------|
| #32 | **Source-generate** | `math::` (abs, ceil, floor, round, fixed, median, pow, sqrt, mod, sign, sin, cos, tan) |
| #36 | **Source-generate** | `type::`, `is::` (is_array, is_bool, etc.) |
| #31 | **Hand-code** | `string::` (length, trim, lowercase, uppercase, replace, split, similarity, distance, is_* validators) |
| #33 | **Hand-code** | `crypto::` (argon2, bcrypt, pbkdf2, scrypt, sha, md5, uuid) |
| #35 | **Hand-code** | `array::` (add, append, prepend, remove, sort, reverse, distinct, union, intersect, contains, filter) |
| #34, #36, #37, #38, #42 | **Mixed** | `session::`, `meta::`, `object::`, `geo::`/`vector::` extras, `search::`, `rand::`, `http::` |

#### Architecture Decision: Handler Classes vs IMethodCallParser

Marten uses `IMethodCallParser` (interface + registration list + `MethodInfo` caching) — necessary for 30+ parser types, user-extensible custom parsers, and composite `ISqlFragment` output. Each parser has `Matches()` + `Parse()` and is registered in `LinqParsing._parsers`.

Dali's functions are simpler: `MethodCallExpression` → `string` (SurrealQL fragment). The handler class pattern (`GeoExpressionHandler.TranslateGeoFunc()`, `TimeExpressionHandler.TranslateTimeFunc()`) already handles this cleanly with a `static string? Translate(string methodName, string[] args)` signature. Adding `IMethodCallParser` would be over-engineering at 100 functions. Handler classes scale well to ~300 functions before a registry pattern pays off.

**Extension point for future scaling:** When the handler count exceeds ~20, convert the `if (DeclaringType?.Name == "Xxx")` chain in `TranslateMethod` to a `List<IExpressionHandler>` with `TryTranslate()` returning `string?`. Each handler class implements the interface; registration becomes `_handlers.Add(new MathExpressionHandler())`. This is the natural graduation path from handler classes to a registry — but only when needed.

### P6: Advanced (Week 8)
| Gap | Deliverable |
|-----|-------------|
| #6 | `IDocumentStore.Advanced` — Clean API, `ResetAllData`, tenant management |
| #7 | `IDiagnostics` — `PreviewCommand`, `ExplainPlan` |
| #15 | `CleanDeletedDocumentsAsync()` |
| #20 | `BulkInsertMode` + store-level `BulkInsertAsync` |
| #29 | `SessionOptions.IsolationLevel`, `Timeout` |

### P7: Auth (Week 9)
| Gap | Deliverable |
|-----|-------------|
| #8 | `DEFINE LOGIN` — `SchemaManager.EnsureLoginAsync()` |
| #9 | `DEFINE TOKEN` — JWT config |
| #10 | `DEFINE SCOPE` — signup/signin flows |

### P8-P9: Query Ops + Polish (Weeks 10-11)
| Gaps | Deliverable |
|------|-------------|
| #16, #40 | `DeletedBefore()`, `CONTAINSALL`/`CONTAINSANY`/`CONTAINSNONE`/`INTERSECTS`/`HAVING`/`ANALYZE` |
| #39 | SurrealQL scripting (`FOR`, `IF/ELSE`, `THROW`, `RETURN BEFORE/AFTER/DIFF`) |
| #45-48, #53-54, #57-58, #60-64 | Minor polish sweep |

### Go/No-Go Gates

| Gate | After | Criteria | Action if NO |
|------|-------|----------|--------------|
| G1 | P1 | `IDocumentOperations` extracted, 1129 tests green, `IEvent` has causation fields | Fix breaks, re-run |
| G2 | P2 | FetchForWriting test passes, watermark persists on restart | Halt if race conditions |
| G3 | P4 | Sharded daemon + composite projection pass | Defer minor sub-gaps |
| G4 | P5 | 100+ functions translate without `NotSupportedException` | Ship partial catalog |
| G5 | P7 | Auth flows E2E | Auth is additive — non-blocking |

### Resource Model

| Scenario | Devs × Time | Deliverable |
|----------|-------------|-------------|
| Recommended | 2 × 1 month | P1-P7 (~55 gaps) |
| Sprint | 1 × 1 week | P1 only (5 gaps) |
| Minimal | 1 × 1 month | P1-P3 (~25 gaps) |

### Completed This Session

| Feature | Gaps Closed | Delivered |
|---------|-------------|-----------|
| Deferred graph ops | — | `Relate()`/`Unrelate()` queued in `SaveChangesAsync`; removed `RelateAsync`/`UnrelateAsync` |
| LINQ query syntax tests | — | 30 tests for `from x in source where select x` |
| Identity map + DocumentTracking | #20 (partial) | `DocumentTracking` enum, `Eject<T>()`, identity map in `LoadAsync<T>` |
| SessionOptions + OpenSessionAsync | #23, #29 | `SessionOptions` class, per-session tracking/tenant/custom listeners |
| Policies.ForAllDocuments | #22 | `DocumentPolicies`, `ForAllDocuments<T>()`, `ForDocumentsOfType<T>()`, `IDocumentPolicy` |
| Gap doc expanded | — | 64 total gaps (10C/32M/22m) from original 27 |
| SurrealDB function mapping (Phases 5a-5f) | #31-42 | 155 SurrealDB functions mapped across 13 handler classes + 2 dictionary dispatch layers |
| ProjMember overhaul (Phase 6) | — | All 155 functions enabled in SELECT/ORDER BY/GROUP BY projections |
| Aggregate MethodCallExpression (Phase 7a) | — | Sum/Min/Max/Average now support function-call lambda bodies |
| Projection progress persistence (Phase 7b) | #17 | AsyncDaemon persists/loads high-water marks from mt_projection_progress |
| Projection sharding (Phase 18) | #26 | AsyncDaemon refactored to per-shard workers with independent health/watermark tracking |
| Custom groupers (Phase 18) | #27 | IAggregateGrouper&lt;TId&gt;, IEventGrouping&lt;TId&gt;, EventGrouping&lt;TId&gt;, CustomGrouping on MultiStreamProjection |
| Composite projections (Phase 18) | #28 | CompositeProjection class with Add/Life fluent API; auto-registration in DocumentStore |
| POCO projection support (Q1) | — | Relaxed `T : Record` → `T : class` across all 5 projection base classes; added POCO identity detection and `SetPocoIdentity()` helper with type conversion (Guid/int/long/string) |
| IProjection Marten DIM overload (Q2) | — | Added `ApplyAsync(IDocumentOperations, IEnumerable<IEvent>, CancellationToken)` as default interface method on `IProjection` |
| POCO projection test suite (Q3) | — | 10/10 tests covering SingleStream, MultiStream, Event, Snapshot projections with POCOs, backward compat, and identity types |
| Phase 0: Shared types extraction | — | Created `samples/AeroDB.Samples.Shared/` with event types, models (Trip/Day/Distance/User/Target), and AeroDB-native projection classes from DaemonTests |
| Phase 1: EventSourcingIntro ported | — | First Marten sample ported to AeroDB — warehouse inventory demo runs correctly (0→100-10+5=95) validating Q1+Q2 |
| Phase 2: DocSamples gap audit | — | Identified 3 new AeroDB gaps (#65-67) via minimal drop-in attempt. 12 Marten APIs verified at parity. |
