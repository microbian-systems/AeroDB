# Dali — Implementation Plan

> MartenDB-style document database and event store on SurrealDB.

**Last modified:** 2026-06-20
**Version:** 0.1.0
**Build:** 0 errors (4 pre-existing stale warnings)
**Tests:** 444 passing, 0 failing, 0 skipped
**Projects:** `Dali`, `Dali.EntityFrameworkCore`, `Dali.Tests`, `WolverineFx.Dali` (complete)
**Samples:** `CryptoTrader` — Dali + Wolverine crypto trading simulation

---

## Legend

| Icon | Meaning |
|------|---------|
| ✅ Done | Implemented, reviewed, approved |
| 🔄 In Progress | Active implementation |
| 📋 Planned | Spec'd but not started |

---

## Overall Progress

| Phase | Tests | Review | Status |
|-------|-------|--------|--------|
| 1. Core Document Store | 8 | — | ✅ Done |
| 2. Test Coverage | 30 | APPROVED | ✅ Done |
| 3. Projections | 37 | APPROVED | ✅ Done |
| 4. Multi-Tenancy + Schema | 49 | APPROVED | ✅ Done |
| 5. Query Translation | 64 | APPROVED | ✅ Done |
| 6. EF Core Bridge | 81 | APPROVED | ✅ Done |
| 7. Modular Config (IConfigureDali + Schema.For) | 91 | APPROVED | ✅ Done |
| 8. Compiled Queries | 104 | APPROVED | ✅ Done |
| 9. Patch/Partial Updates | 110 | APPROVED | ✅ Done |
| 10. Optimistic Concurrency | 116 | APPROVED | ✅ Done |
| 11. Soft Delete | 124 | APPROVED | ✅ Done |
| 12. Database-per-Tenant | 134 | APPROVED | ✅ Done |
| 13. Server-Side Aggregates | 134 | APPROVED | ✅ Done |
| 14. Source Generators | 144 | APPROVED | ✅ Done |
| 15. Schema Modes, Events, RawQL & Functions | 144 | — | ✅ Done |
| 16. Search & Vector Functions | 168 | APPROVED | ✅ Done |
| 17. Graph API (RELATE, traversal, paths, FoF) | 327 | APPROVED | ✅ Done |
| 18. Multi-Database / Schema Support | 444 | — | ✅ Done — `DocumentMapping.Schema()`, `ForkSession()` routing, 26 tests |
| 19. Advanced Low-Level SDK Access | 444 | APPROVED | ✅ Done — `store.Advanced`, `IDaliAdvanced`, 8 tests |
| 20. WolverineFx.Dali Integration | 444 | REVISED (council) | ✅ Done — 26+ src files, IDaliOp, event forwarding, scheduled jobs, 22 integration tests |
| ✚ Cross-cutting: Transactional SaveChanges | 359 | — | ✅ Done |
| ✚ Cross-cutting (logging, ConfigureAwait, ct) | 144 | — | ✅ Done |

---

## Phase 1: Core Document Store ✅

**Tests:** 8

| Layer | What's built |
|-------|-------------|
| Interfaces | `IDocumentStore`, `IQuerySession`, `IDocumentSession`, `IEvents` |
| Sessions | `QuerySession` (read-only), `DocumentSession` (read-write + unit of work) |
| LINQ | `SurrealExpressionVisitor` (Where, OrderBy, Skip, Take, Select, string methods), `SurrealQueryProvider` — `Include()` uses LET variables for single-round-trip eager loading (like Marten's temp tables) |
| Storage | `DocumentStorage` (snake_case table naming), `UnitOfWork` (Added/Modified/Deleted) |
| Events | `EventStore` (Append, StartStream, FetchStream on `mt_events`) |
| Schema | `SchemaManager` (DEFINE TABLE/FIELD/INDEX) |
| DI | `Documents.For()` factory, `StoreOptions.ClientFactory` for embedded clients |

**Key decisions:** Single project, no Dali.Common, no Snowflake — consumer chooses ID strategy. Models extend `SurrealDb.Net.Models.Record` for CBOR compatibility.

---

## Phase 2: Test Coverage ✅

**Tests:** 30 (22 new)

| Area | Tests added |
|------|------------|
| Query translation | OrderBy/OrderByDescending, Where (AND/OR/NOT), FirstOrDefault, Skip, Contains, Count, Any |
| Sessions | Store + load by id, Dirty tracking update, Store multiple, Delete + verify, Session isolation |
| Unit of work | Mixed operations, Batch size, Clear between saves |
| Edge cases | Empty query, Null store exception, Duplicate store |

**Key fixes discovered:** Field name casing bug (visitor used snake_case but CBOR stores PascalCase), Thread safety (shared visitor), CountAsync CBOR deserialization.

---

## Phase 3: Projections ✅

**Tests:** 37 (7 new)

| Component | Description |
|-----------|-------------|
| `IProjection` / `IProjectionContext` | Core projection interfaces |
| `ProjectionLifecycle` | Inline / Async enum |
| `InlineProjection<T>` | Abstract base for inline projections |
| `SingleStreamProjection<T>` | Doc ID derived from event StreamId |
| `MultiStreamProjection<T>` | Cross-stream doc identity |
| `AsyncDaemon` | Background polling worker with high-water mark |
| `DocumentSession` | Wires inline projections into SaveChangesAsync |

---

## Phase 4: Multi-Tenancy + Schema ✅

**Tests:** 49 (12 new)

| Component | Description |
|-----------|-------------|
| `TenancyStyle` | None / Conjoined / DatabasePerTenant |
| Session-level tenant tracking | `SetTenant()`, `ClearTenant()`, `TenantId` property |
| Conjoined filter | Auto-appends `WHERE TenantId = '{tenant}'` in queries |
| Store auto-set | Auto-sets `TenantId` on entities with `TenantId` property |
| Delete validation | Validates entity tenant matches session tenant |
| Schema auto-create | `EnsureDocumentSchemaAsync<T>()`, `EnsureEventSchemaAsync()` |
| SCHEMAFULL tables | Fields use PascalCase (matching CBOR storage) |

---

## Phase 5: Query Translation ✅

**Tests:** 64 (15 new)

| Feature | LINQ → SurrealQL |
|---------|-----------------|
| OrderBy | ThenBy, ThenByDescending |
| Compound conditions | AND (`&&`), OR (`\|\|`), NOT |
| Select | Anonymous types + MemberInit (`new Dto { X = p.X }`) |
| Aggregates | `Sum` → `math::sum`, `Min` → `math::min`, `Max` → `math::max`, `Average` → `math::mean` |
| String functions | `Contains` → `string::contains`, `StartsWith`, `EndsWith` |
| Math operators | `+`, `-`, `*`, `/`, `%` |
| Date functions | `time::year/month/day/wday/hour/minute/second` |
| Search functions | `search::analyze`, `search::score`, `search::rrf` — planned (see backlog)[^1] |
| Vector functions | `vector::distance::knn`, `vector::similarity::cosine` — planned (see backlog)[^1] |
| Full-text match | `@@` operator with `FULLTEXT ANALYZER` + BM25 scoring — planned[^1] |
| KNN operator | `<\|>\|` with `HNSW` index for approximate nearest-neighbor — planned[^1] |
| Hybrid fusion | `search::rrf()` combining BM25 + vector results — planned[^1] |
| First/Single | `FirstOrDefaultAsync` (with/without predicate), `SingleOrDefaultAsync` |
| Table quoting | Backtick-quoted table names for keyword safety |

---

## Phase 6: EF Core Bridge ✅

**Tests:** 81 (17 new)

| Component | Description |
|-----------|-------------|
| `DaliEfCoreTransaction` | Wraps `IDocumentSession` as `IDbContextTransaction` |
| `DaliEfCoreTransactionManager<TDbContext>` | Implements `IDbContextTransactionManager` via `ReplaceService<>()` |
| `ServiceCollectionExtensions` | `AddDaliWithEfCore<TDbContext>()` DI registration |
| Rollback | Clears Dali unit of work (SurrealDB has no native rollback) |

**Project:** `src/Dali.EntityFrameworkCore/Dali.EntityFrameworkCore.csproj` (references `Dali` + `Microsoft.EntityFrameworkCore` 10.0.8)

---

## Cross-Cutting Concerns ✅

| Concern | Implementation |
|---------|---------------|
| `ILogger<T>` everywhere | 14 files, `NullLogger<T>` fallback when no `ILoggerFactory` |
| Log levels | Information (init, commit, projection cycle); Debug (SurrealQL, operations) |
| `ConfigureAwait(false)` | Every `SurrealDb.Net` async call |
| Cancellation tokens | All async methods accept and forward `CancellationToken ct` |
| `StoreOptions.LoggerFactory` | Plumbed through constructor chain to all classes |

---

## Phase 7: Modular Configuration (IConfigureDali + Schema.For) ✅

**Tests:** 91 (10 new)

| Component | Description |
|-----------|-------------|
| `IConfigureDali` | Marker interface — consumers implement, registered in DI, `Configure(StoreOptions)` called during init |
| `SchemaOptions.For<T>()` | Fluent document-level configuration (returns `DocumentMapping<T>`) |
| `DocumentMapping<T>.Index(p => p.Field)` | Simple index on a single field |
| `DocumentMapping<T>.UniqueIndex(p => p.Field)` | Unique index |
| `DocumentMapping<T>.CompositeIndex(p => p.F1, p => p.F2)` | Multi-column composite index |
| `DocumentMapping<T>.UniqueCompositeIndex(p => p.F1, p => p.F2)` | Multi-column unique composite index |
| `DocumentMapping<T>.MultiTenanted()` | Marks document as multi-tenanted |
| `IndexOptions` | `IsUnique()`, `WithName()` overrides on index definition |
| `SchemaManager.EnsureIndexAsync()` | `DEFINE INDEX idx ON TABLE table COLUMNS ... UNIQUE` |
| Auto-apply | All mappings + indices applied during `DocumentStore.InitializeAsync` |
| `ConfigureDali<T>()` | DI extension: `services.ConfigureDali<MyConfig>()` |

**Usage:**
```csharp
// Fluent schema config
var store = Documents.For(o =>
{
    o.Schema.For<Person>()
        .Index(p => p.Email)
        .UniqueIndex(p => p.Ssn)
        .CompositeIndex(p => p.FirstName, p => p.LastName);
});

// Modular DI config
services.ConfigureDali<UserSchemaConfiguration>();
```

---

## Phase 8: Compiled Queries ✅

**Tests:** 104 (13 new)

| Component | Description |
|-----------|-------------|
| `CompiledQuery<T>` | Wrapper holding cached `SurrealQueryResult` from one-time expression translation |
| `CompileQuery<T>(q => q.Where(...))` | Extension on `IDocumentStore` — walks expression tree once |
| `QueryAsync(compiled)` | Extension on `IQuerySession` — executes using cached SurrealQL |
| `QueryFirstOrDefaultAsync(compiled)` | Executes with LIMIT 1 using cached result |
| `CompiledQueryProvider<T>` | Internal execution engine — clones cached result, applies tenant filter, generates SurrealQL |
| `SurrealQueryResult.Clone()` | Deep copy for thread-safe mutation on each execution |

**Usage:**
```csharp
var compiled = store.CompileQuery<Person>(q => q
    .Where(p => p.Age > 25)
    .OrderBy(p => p.Name));

var results = await session.QueryAsync(compiled);
```

---

## Phase 9: Patch/Partial Updates ✅

**Tests:** 110 (6 new)

| Component | Description |
|-----------|-------------|
| `PatchExpression<T>` | Fluent builder: `Set()`, `Increment()`, `Append()`, `Delete()` |
| `SetOperation` | Internal model with PascalCase SurrealQL formatting |
| `session.Patch<Person>(id).Set(p => p.Age, 30).ApplyAsync()` | Extension method on `IDocumentSession` |

**Usage:**
```csharp
session.Patch<Product>(id)
    .Set(p => p.Price, 19.99m)
    .Increment(p => p.Quantity, 5)
    .ApplyAsync();
```

---

## Phase 10: Optimistic Concurrency ✅

**Tests:** 116 (6 new)

| Component | Description |
|-----------|-------------|
| `IVersioned` | Interface with `long Version { get; set; }` |
| `[Version]` attribute | Marks any `long` property as version field |
| `ConcurrencyException` | Thrown on version mismatch with Expected/Actual/DocumentType/DocumentId |
| `StoreOptions.UseOptimisticConcurrency` | Enable/disable (default: false) |
| Version tracking | `_originalVersions` dictionary, cached `PropertyInfo`, `GetVersion()`/`IncrementVersion()` |
| Three-phase save | Check → Increment → Persist |

**Usage:**
```csharp
var store = Documents.For(o => { o.UseOptimisticConcurrency = true; });

public class Customer : Record, IVersioned
{
    public string Name { get; set; } = "";
    public long Version { get; set; }
}
```

---

## Phase 11: Soft Delete ✅

**Tests:** 124 (8 new)

| Component | Description |
|-----------|-------------|
| `ISoftDeleted` | Interface: `DateTimeOffset? DeletedAt`, `bool Deleted` |
| `OperationType.SoftDeleted` | Unit of work flag for soft-delete operations |
| `DocumentSession.Delete<T>()` | Auto-detects `ISoftDeleted`, queues `SoftDeleted` instead of `Deleted` |
| `SurrealQueryProvider.ApplySoftDeleteFilter()` | Appends `WHERE Deleted = false` on all queries for `ISoftDeleted` types |
| `StoreOptions.SoftDeleteEnabled` | Toggle query auto-filter (default: true) |
| `SoftDeleteAsync<T>(session, id)` | Extension for direct soft-delete by record ID |

**Usage:**
```csharp
var session = store.LightweightSession();
session.Delete(softDeletePerson);  // sets Deleted=true, DeletedAt=now instead of removing
await session.SaveChangesAsync();

// Queries automatically exclude soft-deleted documents
var active = await session.Query<SoftDeletePerson>().ToListAsync(); // WHERE Deleted = false
```

---

## Phase 12: Database-per-Tenant ✅

**Tests:** 134 (10 new)

| Component | Description |
|-----------|-------------|
| `DatabasePerTenantSelector` | Per-tenant `ISurrealDbClient` cache; database = `{namespace}_{tenantId}` |
| `DocumentStore.WithTenant()` | Specifies tenant for next session creation |
| `DocumentStore.InitializeAsync` | Creates selector, skips default database connection |
| Session creation | Resolves tenant (WithTenant > DefaultTenantId), uses tenant-scoped client |
| `InternalSessionBase.LoadAsync` | Skips entity-level tenant check (database isolation is sufficient) |
| `DocumentSession.Store/Delete` | Skips TenantId auto-set and tenant check |
| `SurrealQueryProvider` | Skips WHERE TenantId filter (database-level isolation) |
| Client disposal | `IAsyncDisposable` on selector, called in `DocumentStore.DisposeAsync` |

**Usage:**
```csharp
var store = Documents.For(o =>
{
    o.TenancyStyle = TenancyStyle.DatabasePerTenant;
    o.Namespace = "myapp";
});

// Each tenant gets its own database: myapp_tenant-a, myapp_tenant-b
await using var sessionA = await store.WithTenant("tenant-a").LightweightSessionAsync();
await using var sessionB = await store.WithTenant("tenant-b").QuerySessionAsync();
```

---

---

## Phase 13: Server-Side Aggregates ✅

**Tests:** 134

| Component | Description |
|-----------|-------------|
| Root cause fix | Removed `query.Projection = "*"` override in `AggregateAsync` — visitor's `math::sum(field)` now flows to SurrealQL |
| `GROUP ALL` | Added to `SurrealQueryResult` — proper SurrealQL aggregate clause |
| `CountAsync` server-side | Uses `SELECT count() ... GROUP ALL` instead of `SELECT *` + client-side count |
| `[CborProperty]` DTOs | SumResultDto, MinResultDto, MaxResultDto, MeanResultDto, CountResultDto for CBOR deserialization |
| Expression tree building | `ISableQueryable` aggregate methods now build `Expression.Call` trees for visitor dispatch |

---

## Phase 14: Source Generators ✅

**Tests:** 144 (10 new)
**Generated types:** 9 Record subclasses

| Component | Description |
|-----------|-------------|
| `DaliDocumentGenerator` | `IIncrementalGenerator` — scans `Record` subclasses, generates per-type `{Type}Metadata.g.cs` |
| `DaliDocumentAttribute` | Opt-out: `[DaliDocument(SkipGeneration = true)]` |
| `MetadataRegistry` | Static `ConcurrentDictionary` registry: generated types self-register via static constructor |
| `MetadataDispatch` | Registry-first, reflection-fallback dispatch: `GetTableName()`, `HasTenantId()`, `GetVersionFieldName()` |
| Generated per type | `TableName` constant, `HasTenantId`/`HasVersion` constants, `GetTenantId`/`GetVersion`/`SetVersion`/`GetRecordId` delegates |
| Runtime retrofit | `SurrealQueryProvider.HasTenantProperty` → `MetadataDispatch.HasTenantId`, `InternalSessionBase.GetVersion` → dispatch, `DocumentSession.Snake` → `GetTableName` |

**Project:** `src/Dali.SourceGenerators/` (netstandard2.0, Microsoft.CodeAnalysis.CSharp 4.11.0, analyzer reference in Dali.csproj)

---

## Phase 15: Schema Modes, Native Events, RawQL & Functions ✅

**Tests:** 144

| Component | Description |
|-----------|-------------|
| `SchemaMode` enum | `Strict` (SCHEMAFULL) / `Flexible` (SCHEMALESS) — configurable per document type |
| `DocumentMapping<T>.SetSchemaMode()` | Fluent API to set schema mode for a type |
| `SchemaManager.GetSchemaSurql()` | Emits `SCHEMAFULL` or `SCHEMALESS` based on mode |
| `IQuerySession.RawQueryAsync<T>()` | Raw SurrealQL query returning typed results |
| `IQuerySession.ExecuteSqlAsync()` | Execute non-query SurrealQL (CREATE, UPDATE, DEFINE, etc.) |
| `EventTriggerDefinition` | Model for SurrealDB `DEFINE EVENT` triggers (`Name`, `Table`, `WhenCondition`, `Action`, `Async`, `Retry`, `MaxDepth`) |
| `EventTriggerManager` | `EnsureTriggerAsync()`, `AlterTriggerAsync()`, `RemoveTriggerAsync()` |
| `EventTriggerOptions` | Config in `StoreOptions.Events.Triggers` with `AddTrigger()` fluent API + `AutoCreateTriggers` |
| `SurrealFunction` | Model for SurrealDB `DEFINE FUNCTION` |
| `FunctionManager` | `EnsureFunctionAsync()`, `RemoveFunctionAsync()` |
| `FunctionOptions` | Config in `StoreOptions.Functions` with `Register()` fluent API + `AutoCreateFunctions` |
| `StoreOptions.Functions` | User-defined function config section |
| `DocumentStore.InitializeAsync` | Auto-applies schema modes, event triggers, and user-defined functions during initialization |

**Usage:**
```csharp
var store = Documents.For(o =>
{
    // Schema mode: per document type
    o.Schema.For<Person>()
        .SetSchemaMode(SchemaMode.Flexible)
        .Index(p => p.Email);

    // Native event triggers (separate from Marten-style event sourcing)
    o.Events.Triggers.AddTrigger(
        name: "user_created",
        table: "user",
        action: "CREATE audit SET event = $event, table_name = 'user'",
        whenCondition: "$event = 'CREATE'"
    );

    // User-defined functions
    o.Functions.Register("fn::greet",
        body: "RETURN 'Hello, ' + $name;",
        parameters: "$name: string"
    );
});

// Raw SQL access on any session
await using var session = store.QuerySession();
var adults = await session.RawQueryAsync<Person>(
    "SELECT * FROM person WHERE age > $minAge",
    new Dictionary<string, object?> { ["minAge"] = 18 }
);
```

---

## Small Backlog Items (complete) ✅

| Item | Files |
|------|-------|
| Scoped event triggers | `EventTriggerOptions.AddTrigger<T>()` — auto-resolves table from `typeof(T)` |
| Function arg validation | `SurrealFunctionParameter`, `SurrealFunction.ParametersTyped`, `FunctionOptions.Register()` overload |
| IDocumentListener | `IDocumentSessionListener` interface + wiring in `SaveChangesAsync` |
| Batch operations | `BulkOperations.BulkInsertAsync<T>()`, `.BulkDeleteAsync<T>()` |

---

## Search & Vector Functions ✅

**Tests:** ~24 (cumulative: 168)

| Component | Description |
|-----------|-------------|
| `SurrealFunctions` | Static class: `Score(n)`, `VectorDistanceKnn()`, `VectorSimilarityCosine(a,b)` — expression-tree only |
| Expression visitor | Translates `SurrealFunctions.*` → `search::score()`, `vector::distance::knn()`, `vector::similarity::cosine()` |
| `SearchExtensions` | `MatchTextAsync<T>(fields, query, limit)` — full-text `@@` operator with weighted multi-field |
| `SearchExtensions` | `MatchKnnAsync<T>(field, vector, limit, candidates)` — KNN `<\|>\|` operator |
| `SearchExtensions` | `HybridSearchAsync<T>(config)` — RRF fusion of full-text + vector results |
| `HybridSearchConfig` | Configuration model: Query, QueryVector, TextFields, VectorField, VectorCandidates, RrfK, RrfLimit |

**References:** SurrealDB hybrid fusion blog post[^1]. The SurrealDB docs search engine uses `search::score()` with BM25 weighting, `vector::distance::knn()` via HNSW, and `search::rrf()` for ranked fusion.

## Phase 17: Graph API (RELATE, Traversal, Paths, FoF) ✅ Complete

SurrealDB treats graph edges as first-class records with `RELATE`, enabling typed connections with metadata that can be traversed via directed path syntax (`->`, `<-`), recursive depth queries (`@.{n}`), and built-in shortest-path algorithms (`+shortest`). The Surrealist Graph view[^2] visualizes these relationships as interactive node-edge diagrams — see that post for rich examples of company org charts, rail networks, rock-paper-scissors cycles, and EU treaty memberships expressed as graph queries.

**Tests:** ~156 (cumulative: 324) — including 10 Friend-of-Friends integration tests with 1000-person Bogus social graph

| Component | Description |
|-----------|-------------|
| `EdgeRecord` | Abstract base for all edge types — extends `Record` with `In`/`Out` `RecordId` properties |
| `GraphNode` / `GraphEdge` | Lightweight models for wildcard (`->?->?`) and path results |
| `GraphPath` | Structured result with `Nodes` + `Edges` arrays for `+path` traversals |
| `IGraphQuery<TNode>` | Fluent interface: `Out<T>()`, `In<T>()`, `Both<T>()`, `OutAny()`, `InAny()`, `AnyEdge()` |
| `IGraphQuery<T>.Depth(n)` | Fixed-depth recursive traversal — maps to `@.{n}` |
| `IGraphQuery<T>.Depth(min, max)` | Range-depth — maps to `@.{min..max}` |
| `IGraphQuery<T>.ShortestPath(id)` | Shortest path algorithm — maps to `@.{..+shortest=id}` |
| `IGraphQuery<T>.ReturnPath()` | Path collection — maps to `+path` |
| `IGraphQuery<T>.CollectAll()` | Unique node collection — maps to `+collect` |
| `IGraphQuery<T>.IncludeIntermediate()` | Include intermediate nodes — maps to `(+)` |
| `IGraphQuery<T>.Fetch()` | Eager load relations — maps to `FETCH` clause |
| `GraphQueryPlan` | Immutable intermediate representation of graph steps |
| `GraphSurrealQLGenerator` | `GraphQueryPlan` → SurrealQL string |
| `GraphResultDeserializer` | CBOR response → `List<T>` or `List<GraphPath>` |
| `GraphQueryProvider` | Executes plan via `session.RawQuery` |
| `IDocumentSession.RelateAsync<TEdge>()` | Create graph edge with typed metadata |
| `IDocumentSession.UnrelateAsync()` | Remove a graph edge by ID |
| `IQuerySession.Graph<T>()` | Entry point for graph traversal queries |
| `EdgeMapping<TEdge, TIn, TOut>` | Fluent schema config for edge tables (`DEFINE TABLE ... TYPE RELATION IN ... OUT ...`) |
| `SchemaOptions.Edge<TEdge, TIn, TOut>()` | Registration extension for edge schemas |

### Architecture

```
IGraphQuery<TNode>                          # Public fluent interface
       ↓
GraphQueryBuilder                           # Mutable builder, accumulates steps
       ↓
GraphQueryPlan (immutable)                  # IR: List<GraphStep> + flags
       ↓
GraphSurrealQLGenerator                     # Plan → SurrealQL string
       ↓
GraphQueryProvider                          # Calls session.RawQuery
       ↓
GraphResultDeserializer                     # CBOR → List<T> / List<GraphPath>
```

### Planned Usage

```csharp
// ─── RELATE ───
await session.RelateAsync<WorksIn, Person, Team>(
    RecordId.Of<Person>("alice"),
    RecordId.Of<Team>("alpha"),
    new WorksIn { Role = "Lead", Since = DateTimeOffset.UtcNow });

// ─── Single-hop traversal ───
var team = await query.Graph<Person>()
    .Out<Team>("works_in")
    .FirstOrDefaultAsync();
// → SELECT ->works_in->team.* FROM person LIMIT 1;

// ─── Multi-hop ───
var projects = await query.Graph<Person>()
    .Out<Team>("works_in")
    .Out<Project>("works_on")
    .ToListAsync();
// → SELECT ->works_in->team->works_on->project.* FROM person;

// ─── Recursive depth ───
var ancestors = await query.Graph<Person>()
    .In<Person>("child_of")
    .Depth(2, 5)
    .ToListAsync();
// → SELECT @.{2..5}<-child_of<-person.* FROM person;

// ─── Shortest path ───
var paths = await query.Graph<Person>()
    .Out<Person>("knows")
    .ShortestPath(RecordId.Of<Person>("charlie"))
    .ReturnPath()
    .ToPathListAsync();
// → SELECT @.{..+shortest=person:charlie}->knows->person.+path FROM person;

// ─── Edges with filter ───
var leads = await query.Graph<Person>()
    .Out<Team>("works_in")
    .Where(t => t.Name == "Alpha Team")
    .ToListAsync();
// → SELECT ->works_in->team.* FROM person WHERE ->works_in->team.Name = 'Alpha Team';

// ─── Fetch eager ───
var personWithTeam = await query.Graph<Person>()
    .Out<Team>("works_in")
    .Fetch("works_in")
    .FirstOrDefaultAsync();
// → SELECT *, ->works_in AS works_in FROM person FETCH works_in LIMIT 1;
```

### Schema Integration

```csharp
var store = Documents.For(o =>
{
    o.Schema.Edge<WorksIn, Person, Team>(edge =>
    {
        edge.SchemaMode(SchemaMode.Strict);
        edge.Index(e => e.Role);
    });
});
// → DEFINE TABLE works_in TYPE RELATION IN person OUT team SCHEMAFULL;
// → DEFINE FIELD role ON TABLE works_in TYPE string;
// → DEFINE INDEX idx_works_in_role ON TABLE works_in COLUMNS role;
```

### Test Plan (~30 new tests)

| Test Area | Count | What It Covers |
|-----------|-------|----------------|
| RELATE | 6 | Create edge with metadata, create edge without metadata, duplicate edge prevention, edge ID returned, type checking, null handling |
| Single-hop traversal | 6 | `Out<T>()` returns correct type, `In<T>()` works backward, empty results, multiple edges of same type, chained after Where, null navigation |
| Multi-hop traversal | 4 | 2-hop, 3-hop, wildcard `AnyEdge()`, mixed direction (out then in) |
| Depth control | 4 | Fixed depth, range depth, open-ended, depth out of bounds |
| Shortest path | 3 | Path exists, no path exists, with ReturnPath |
| Path collection | 2 | `+path`, `+collect` |
| Edge schema | 3 | EdgeMapping creates correct SurrealQL, schema mode respected, index on edge field |
| FETCH / eager load | 2 | Fetch basic, fetch multiple relations |
| Edge metadata access | 2 | Edge properties accessible, edge typed as correct subclass |

---

## Phase 18: Multi-Database / Schema Support 📋 Planned

**Tests:** 324 (planned: ~25 new)

SurrealDB's `NAMESPACE → DATABASE` hierarchy maps to PostgreSQL's `DATABASE → SCHEMA` model. Each SurrealDB `DATABASE` fully isolates its tables, fields, indexes, events, and functions. Dali lets you map document types to different databases via `DocumentMapping<T>.Schema()`.

### Critical Design Decision: ForkSession, Not Inline `USE DB`

The council discovered that SurrealDB's `USE` is an **RPC method**, not SurrealQL. The originally proposed `"USE DB sales; SELECT ..."` string-concatenation approach is **technically invalid**. Instead, Dali uses `ForkSession()` to clone the parent session, then calls `.Use(ns, db)` on the fork — mirroring the proven `DatabasePerTenantSelector` pattern.

| Approach | Viable? | Reason |
|----------|---------|--------|
| Inline `"USE DB sales; SELECT ..."` | ❌ | `USE` is an RPC method, not SurrealQL — cannot appear in query strings |
| `ForkSession() + Use(ns, db)` | ✅ | Proven pattern — same as `DatabasePerTenantSelector` |
| Separate client pool per database | ⚠️ Overkill | Forked sessions share the same connection pool |

### Constraints (Council-Approved)

| Rule | Rationale |
|------|-----------|
| **Cross-DB queries rejected at translation** | SurrealDB has no cross-database queries. LINQ expressions spanning types from different schemas throw `InvalidOperationException`. |
| **Multi-DB transactions rejected** | `SaveChangesAsync` groups operations by database; throws if >1 group. Users needing cross-DB consistency must orchestrate compensating sagas. |
| **Schema auto-creation is opt-in** (`AutoCreateDatabases = false`) | `DEFINE DATABASE` is a high-privilege operation. Production credentials often cannot create databases. |
| **Fluent API: `.Schema("sales")` not `.Database("sales")`** | Avoids naming collision with `StoreOptions.Database` (the default connection database). Aligns with PostgreSQL/Marten terminology. |
| **Default schema = implicit null → uses `StoreOptions.Database`** | Unconfigured document types continue to use the default database. Passing `.Schema(null)` explicitly resets to default. |

### Components

| Component | Description |
|-----------|-------------|
| `DocumentMapping.SchemaName` | `string?` — `null` means "use default database" |
| `DocumentMapping<T>.Schema(string?)` | Fluent API for per-type schema routing |
| `SchemaManager.EnsureDatabaseAsync()` | `DEFINE DATABASE {name}` for each unique schema |
| `SchemaOptions.AutoCreateDatabases` | Opt-in toggle (default: false) |
| `SchemaTarget` | Internal record struct `(string Database, string Table)` — threaded through all query/persistence call sites |
| `InternalSessionBase.GetSessionForSchemaAsync(string?)` | `ForkSession() + Use(ns, db)` — caches forked sessions in `ConcurrentDictionary` |
| `SurrealQueryProvider` | Routes queries to correct forked session based on `SchemaTarget` |
| `DocumentSession.SaveChangesAsync` | Groups `UnitOfWork` operations by target database; rejects multi-DB groups |
| `SchemaManager.EnsureDocumentSchemaAsync` | Groups document mappings by schema during initialization; creates on correct session |

### Architecture

```
DocumentStore.InitializeAsync
  └→ SchemaManager
       └→ EnsureDatabaseAsync(session, "sales")    only if AutoCreateDatabases
       └→ EnsureDatabaseAsync(session, "hr")
       └→ EnsureDocumentSchemaAsync<Invoice>(session, "sales")
       └→ EnsureDocumentSchemaAsync<Employee>(session, "hr")

IQuerySession / IDocumentSession
  ├→ GetSessionForSchema("sales")    ForkSession() + Use(ns, "sales")
  ├→ GetSessionForSchema("hr")       ForkSession() + Use(ns, "hr")
  └→ GetSessionForSchema(null)       parent session (default db)

Query execution:
  └→ SurrealQueryProvider:
       ├→ Resolves SchemaTarget(database, table) from DocumentMapping
       ├→ Routes to correct forked session
       └→ Executes on isolated session

SaveChangesAsync:
  └→ Groups UnitOfWork.Operations by target database
     ├→ if groups.Count > 1 → throw InvalidOperationException
     └→ else → execute on correct forked session
```

### Planned Usage

```csharp
var store = Documents.For(o =>
{
    o.Schema.For<Invoice>()
        .Schema("sales")
        .Index(i => i.Total);

    o.Schema.For<Employee>()
        .Schema("hr");

    o.Schema.For<Product>();        // implicit → uses StoreOptions.Database

    // Opt-in database auto-creation (high privilege)
    // o.Schema.AutoCreateDatabases = true;
});

await using var session = store.QuerySession();

// Automatically routes to DB "sales" via forked session
var invoices = await session.Query<Invoice>()
    .Where(i => i.Total > 100)
    .ToListAsync();

// Automatically routes to DB "hr" via another forked session
var engineers = await session.Query<Employee>()
    .Where(e => e.Department == "Engineering")
    .ToListAsync();

// Cross-DB LINQ query → throws InvalidOperationException at translation
// var result = from i in session.Query<Invoice>()
//              join e in session.Query<Employee>() on ...
//              → throws: "Cross-database LINQ queries are not supported."
```

### Call Sites Requiring SchemaTarget Threading (~15-20 sites)

| File | Change |
|------|--------|
| `DocumentMapping.cs` | Add `SchemaName` property + `.Schema()` fluent method |
| `DocumentMapping` (abstract) | Add `internal abstract string? SchemaName { get; }` |
| `MetadataDispatch.cs` | Add `GetSchemaTarget(Type)` — returns `(database, table)` |
| `SurrealQueryProvider.cs` | Resolve `SchemaTarget`, route to correct forked session |
| `DocumentSession.cs` | `SaveChangesAsync`: group ops by schema, reject cross-DB |
| `DocumentSession.cs` | `CheckConcurrencyAsync`: use correct schema session |
| `InternalSessionBase.cs` | `GetSessionForSchemaAsync()`, `_schemaSessions` cache |
| `SchemaManager.cs` | `EnsureDocumentSchemaAsync`: accept schema param, group by schema |
| `DocumentStore.cs` | `InitializeAsync`: group mappings by schema, create per-schema |

### Test Plan (~25 new tests)

| Test Area | Count | What It Covers |
|-----------|-------|----------------|
| Schema config | 4 | `.Schema("name")` set/clear, null default, multiple types different schemas |
| ForkSession isolation | 4 | Operations on one schema don't leak to another, session caching |
| Query routing | 4 | Query executes on correct forked session, correct DB targeted |
| Cross-DB query rejection | 3 | LINQ join across schemas throws, single-type query in non-default schema works, mixed expression tree |
| SaveChanges grouping | 4 | Single-DB save works, multi-DB save throws, mixed operations grouped correctly |
| Schema auto-creation | 3 | `AutoCreateDatabases = true` creates DBs, `false` skips, `DEFINE DATABASE` error handling |
| SchemaManager EnsureDocument | 3 | Schema param passed correctly, index creation in correct DB, multiple schemas in one init |

---

## Backlog (Future)

| Area | Description | Priority |
|------|-------------|----------|
| **CI/CD** | GitHub Actions: build, test, package, publish NuGet | Medium |
| **Live projections** | SurrealDB `LIVE SELECT` → real-time projection updates | Medium |
| **Subscriptions** | Real-time event subscriptions via LIVE SELECT | Medium |
| **IDocumentListener** | Marten-style hooks (`BeforeSave`, `AfterSave`, etc.) | Small |
| **Batch operations** | Bulk insert/delete with chunked transactions | Small |
| **Search & vector functions** | LINQ wrappers for `search::analyze`, `search::score`, `search::rrf`, `vector::distance::knn`, `vector::similarity::cosine`, `@@` match operator, and `<\|>\|` KNN operator — see SurrealDB's hybrid fusion pattern[^1] | Medium |
| **CI/CD** | GitHub Actions: build, test, package, publish NuGet | Medium |
| **Scoped event triggers** | Allow binding event triggers to specific document types (auto-resolve table name) | Small |
| **Function argument validation** | Fluent API for typed function parameter definitions instead of raw strings | Small |
| **Event trigger scaffolding** | `dotnet dali trigger add` CLI command for quick trigger creation | Small |
| **Edge metadata in traversal** | Include edge properties in traversal results (`SELECT *, ->edge AS _edge, ->edge->target.*`) | Medium |
| **Graph projection** | `Select()` on graph queries to shape output beyond node-only results | Medium |
| **Graph query composition** | Reusable partial graph query definitions (like compiled queries for graph) | Small |
| **Performance benchmarks** | Benchmark graph traversal vs RawQuery baseline | Medium |

---

## Configuration

```csharp
var store = Documents.For(o =>
{
    // Remote connection
    o.Endpoint = "http://localhost:8000";
    o.Namespace = "myapp";
    o.Database = "mydb";

    // Or embedded (for testing)
    // o.ClientFactory = () => new SurrealDbMemoryClient();

    // Logging (optional — null = no-op)
    o.LoggerFactory = loggerFactory;
    o.MinimumLogLevel = LogLevel.Debug;

    // Schema
    o.Schema.AutoCreate = true;        // auto-create document schemas on init

    // Events
    o.Events.Enabled = true;

    // Native event triggers (DEFINE EVENT — database-level triggers)
    o.Events.Triggers.AddTrigger("user_created", "user",
        action: "CREATE audit SET event = $event, table_name = 'user'",
        whenCondition: "$event = 'CREATE'");

    // User-defined functions (DEFINE FUNCTION)
    o.Functions.Register("fn::greet",
        body: "RETURN 'Hello, ' + $name;",
        parameters: "$name: string");

    // Schema mode (per document type)
    o.Schema.For<Person>().SetSchemaMode(SchemaMode.Flexible);

    // Tenancy
    o.TenancyStyle = TenancyStyle.Conjoined;
    o.DefaultTenantId = "default";
});
```

---

---

## Council Audit (Phase 14-15) — Metadata Wiring Audit

After Phase 14 (Source Generators), a codebase-wide audit was conducted to verify that generated metadata is consumed everywhere it could eliminate reflection. Finding below.

### Audit Results

| Issue | Severity | Finding | Fix |
|-------|----------|---------|-----|
| **A** | Low | 14/18 table name call sites bypass `MetadataDispatch` — call `Snake()` directly | Route through `MetadataDispatch.GetTableName()` |
| **B** | Medium | 3/5 tenant call sites use raw `GetProperty("TenantId")` | Add `SetTenantId` to `ITypeMetadata<T>`, consume in Store/Delete/Load |
| **C** | Medium | Version read/write uses `GetProperty(name).GetValue/SetValue` even after resolving name from metadata | Add untyped accessor delegates to `ITypeMetadata` |
| **D** | N/A | Closed — not a finding (CompiledQuery tenants/soft-delete cannot be pre-computed) | — |
| **E** | N/A | Closed — not a finding (Dali's `ApplyEvents()` avoids Marten's convention dispatch) | — |
| **F** | Medium | Generated typed methods (`GetTenantId`, `GetVersion`, `SetVersion`, `GetRecordId`) never consumed | Side-effect of fixing B+C |

### Fix Progress

| Fix | Status | File Changes |
|-----|--------|-------------|
| A: Table name routing | ✅ Done | ExpressionVisitor, SurrealQueryProvider, SchemaManager, PatchExpression, SoftDeleteExtensions, InlineProjection, DocumentStorage |
| B: Tenant reflection | ✅ Done | MetadataRegistry (+SetTenantId), DaliDocumentGenerator, DocumentSession, InternalSessionBase |
| C: Version accessor | ✅ Done | MetadataRegistry (+untyped delegates), DaliDocumentGenerator, InternalSessionBase |
| F: Typed methods | ✅ Auto-resolved by B+C | Side-effect |

## Phase 19: Advanced Low-Level SDK Access 📋

**Council review:** APPROVED (unanimous Q1-Q3, majority Q4, partial consensus Q5)

**Goal:** Expose the underlying `SurrealDbClient` (singleton) and `SurrealDbSession` (scoped/transient) from `SurrealDb.Net` through a structured `store.Advanced` property for advanced scenarios the library does not abstract.

### Lifetime Model

Per SurrealDb.Net documentation:
| Class | Singleton | Scoped | Transient |
|---|---|---|---|
| `SurrealDbClient` | ✅ | ❌ | ❌ |
| `SurrealDbSession` | ❌ | ✅ | ✅ |

### API

```csharp
public interface IDaliAdvanced
{
    ISurrealDbClient SurrealDbClient { get; }
    Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default);
    Task<ISurrealDbSession> CreateSessionAsync(string ns, string db, CancellationToken ct = default);
}

public interface IDocumentStore : IAsyncDisposable
{
    ISurrealDbClient Client { get; }        // kept, soft [Obsolete]
    IDaliAdvanced Advanced { get; }         // new
}
```

### Implementation Plan

| Step | File | Change |
|---|---|---|
| 1 | `src/Dali/IDaliAdvanced.cs` | New interface: `SurrealDbClient`, `CreateSessionAsync` (2 overloads) |
| 2 | `src/Dali/DaliAdvanced.cs` | Internal implementation: delegates to `DocumentStore.Client`, calls `Client.CreateSession()` + `.Use()` |
| 3 | `src/Dali/IDocumentStore.cs` | Add `IDaliAdvanced Advanced { get; }` |
| 4 | `src/Dali/DocumentStore.cs` | Add `Lazy<IDaliAdvanced>` field, `Advanced` property |
| 5 | Tests | Verify: Client same reference, session creates distinct sessions, session is configured with correct ns/db, disposal works |
| 6 | DI | Add `registerRawTypes: true` overload on `AddDali()` with `TryAdd*` semantics |

### Test Plan (8 tests)

| # | Test | What it verifies |
|---|---|---|
| 1 | `Advanced_SurrealDbClient_ReturnsStoreClient` | `store.Advanced.SurrealDbClient` returns same reference as `store.Client` |
| 2 | `Advanced_CreateSession_ReturnsSession` | Creates a non-null `ISurrealDbSession` |
| 3 | `Advanced_CreateSession_IsUsableForRawQuery` | Can execute a raw query on the session |
| 4 | `Advanced_CreateSession_NsDb_ConfiguresSession` | Session is configured with given namespace and database |
| 5 | `Advanced_CreateSession_Dispose_ReleasesResources` | Session can be disposed without error |
| 6 | `Advanced_CreateSession_ThrowsIfNotInitialized` | Throws before `InitializeAsync` |
| 7 | `Advanced_MultipleSessions_AreIndependent` | Two `CreateSessionAsync()` calls produce independent sessions |
| 8 | `Client_Obsolete_HasMessage` | `[Obsolete]` message points to `Advanced.SurrealDbClient` |

### Session Ownership

Sessions from `Advanced.CreateSessionAsync()` are **not managed by Dali**. Callers must `await using` the returned session. This is enforced by documentation only (no compile-time guard).

### DI Integration

- Default `AddDali()`: Dali types only
- `AddDali(options, registerRawTypes: true)`: also registers `ISurrealDbClient` (singleton) and `ISurrealDbSession` (scoped factory)
- Users can also call `services.AddSurreal()` separately for full control

## ✚ Cross-Cutting: Transactional SaveChanges ✅

**Tests:** 359

**Summary:** `DocumentSession.SaveChangesAsync` now wraps all persistence operations in a SurrealDB transaction using the SDK's `BeginTransaction()`/`Commit()`/`Cancel()` API. The SurrealDB connection is shared — per-entity SDK calls automatically participate in the transaction. This gives full ACID semantics for document operations.

**Changes:**
- `DocumentSession.SaveChangesAsync` — BeginTransaction before persistence, Commit on success, Cancel on exception
- `IDocumentSessionListener` — added `BeforeCommitAsync` + `AfterCommitAsync` hooks for Wolverine outbox integration
- `CheckConcurrencyAsync`, `CreateEntityAsync`, `UpsertRecordAsync` — accept `ISurrealDbSession` parameter
- 6 new integration tests in `DocumentSessionTransactionTests.cs`

**Hook lifecycle:**
```
BeforeSaveChangesAsync → [BEGIN TX] → per-entity ops → events + projections
  → AfterSaveChangesAsync → BeforeCommitAsync → [COMMIT] → AfterCommitAsync
```

## Phase 20: WolverineFx.Dali Integration 📋

**Council review:** REVISED (2026-06-20) — full review with Wolverine.Marten codebase exploration. Plan validated architecturally. Phase 20b (Transactional SaveChanges) ✅ completed — blocker removed. Ready for Phase 20a (DaliMessageStore + Transport).

**Status:** Plan updated with council findings. Phase 20 now blocked on Dali core transactional batching (Phases 20a/20b).

**Goal:** Create a `WolverineFx.Dali` NuGet package (within this repo at `src/WolverineFx.Dali/`) that provides full Wolverine persistence parity with `WolverineFx.Marten`: saga storage, transactional middleware, outbox, projection distribution, and message persistence + transport (inbox/outbox, durable queues).

> **Saga pattern is already in Wolverine core** — Wolverine has a built-in `Saga` base class with state lifecycle and handler dispatch. Store packages only provide the persistence backend. `WolverineFx.Dali` implements `ISagaStorage` — no saga framework reinvention needed.

### Architecture

```
WolverineFx.Dali
├── DaliMessageStore : IMessageStore
│   ├── IMessageInbox — incoming envelope CRUD, poll queries, claim, reassign
│   ├── IMessageOutbox — outgoing envelope storage
│   ├── IDeadLetters — dead letter queue
│   ├── INodeAgentPersistence — durability agent leader election
│   ├── IListenerStore — dynamic listener registration (NullListenerStore initially)
│   ├── IMessageStoreAdmin — schema migration
│   ├── IScheduledMessages — delayed message scheduling
│   │   All operations use ISurrealDbClient.RawQuery() (standalone)
│   │   Schema: self-owned via RawQuery DEFINE TABLE IF NOT EXISTS
│   │   Tables: wolverine_incoming_envelopes, wolverine_outgoing_envelopes,
│   │           wolverine_dead_letters, wolverine_nodes
│   │   Claim pattern: UPDATE ... RETURN BEFORE (CAS-based, single round-trip)
│   └── DaliEnvelope.cs — Envelope SurrealDB record type
│
├── DaliTransport : ITransport (scheme: dali://)
│   ├── DaliQueueListener — poll loop + atomic CAS claim
│   ├── DaliQueueSender — INSERT into outgoing table
│   └── DaliTransportOptions — poll interval, live query toggle
│
├── DaliIntegration : IWolverineExtension
│   └── MVC-style registration: codegen sources, handler policies, transport
│
├── Codegen Frames (mirrors WolverineFx.Marten)
│   ├── OpenDaliSessionFrame
│   ├── DaliSessionSaveChangesFrame
│   ├── FlushDaliOutgoingMessagesFrame
│   └── PrimeScopedDocumentSessionFrame
│
├── DaliPersistenceFrameProvider : IPersistenceFrameProvider
│   ├── Transaction support: session + SaveChanges + FlushOutgoing
│   └── Load/Insert/Update/Delete/Store frames for saga ops
│
├── DaliSagaStorage : ISagaStorage — saga persistence via IDocumentSession
│
├── Outbox Enrollment
│   ├── DaliOutboxedSessionFactory — creates enrolled sessions
│   ├── DaliEnvelopeTransaction : IEnvelopeTransaction
│   ├── FlushOutgoingMessagesOnDaliCommit : IDocumentSessionListener
│   │   ├── BeforeSaveChangesAsync → marks inbox handled
│   │   └── AfterCommitAsync → FlushOutgoingMessagesAsync()
│   └── ScopedDocumentSessionHolder — DI scope priming
│
├── DaliBackedPersistenceMarker : IVariableSource
│
├── DaliSagaStoreDiagnostics : ISagaStoreDiagnostics
│
└── DaliProjectionCoordinator — projection distribution (future)
```

### Envelope Storage Schema

```surql
DEFINE TABLE wolverine_incoming_envelopes SCHEMAFULL;
DEFINE FIELD id ON wolverine_incoming_envelopes TYPE string;
DEFINE FIELD status ON wolverine_incoming_envelopes TYPE string;
DEFINE FIELD owner_id ON wolverine_incoming_envelopes TYPE int;
DEFINE FIELD execution_time ON wolverine_incoming_envelopes TYPE datetime;
DEFINE FIELD attempts ON wolverine_incoming_envelopes TYPE int;
DEFINE FIELD body ON wolverine_incoming_envelopes TYPE bytes;
DEFINE FIELD message_type ON wolverine_incoming_envelopes TYPE string;
DEFINE INDEX idx_incoming_status ON wolverine_incoming_envelopes FIELDS status;
```

### Durable Queue Claim Pattern

Single atomic SurrealQL statement (analog of PostgreSQL `SELECT ... FOR UPDATE SKIP LOCKED`):

```surql
UPDATE wolverine_incoming_envelopes
SET owner_id = $nodeId, status = 'Incoming'
WHERE status = 'Scheduled' AND execution_time <= time::now()
ORDER BY execution_time ASC LIMIT $batchSize
RETURN BEFORE
```

### Transport Wake-up

| Mode | Mechanism | Default |
|------|-----------|---------|
| **Polling** | Configurable interval (default 5s) | ✅ Default |
| **Live Query** | SurrealDB WebSocket `LIVE SELECT` push | ❌ Opt-in |

### Envelope Coupling Strategy (Critical Dali Core Change)

**Problem:** Dali's `SaveChangesAsync` currently sends per-entity SDK calls (no multi-statement batching, no `BEGIN`/`COMMIT`). Envelope writes (from `DaliEnvelopeTransaction`) would execute as separate round trips — no transactional atomicity with domain data changes.

**Solution:** Refactor `SaveChangesAsync` to generate a single batched SurrealQL script wrapped in `BEGIN TRANSACTION`/`COMMIT TRANSACTION`:

```surql
BEGIN TRANSACTION;

-- Domain operations (from tracked entities)
CREATE person:⟨id⟩ CONTENT { name: "Alice", age: 30 };
UPDATE person:⟨id2⟩ SET age = 31;
DELETE product:⟨id3⟩;

-- Envelope operations (queued via PendingEnvelopeOperations)
CREATE wolverine_incoming_envelopes:⟨id⟩ CONTENT { ... };
UPDATE wolverine_incoming_envelopes:⟨id2⟩ SET status = 'Handled';
INSERT INTO wolverine_outgoing_envelopes { ... };

COMMIT TRANSACTION;
```

**Dali core changes required (Phase 20b):**

```csharp
// DocumentSession gains:
internal List<Func<StringBuilder, CancellationToken, Task>> PendingOperations { get; }

// SaveChangesAsync refactored:
protected override async Task SaveChangesAsync(CancellationToken ct)
{
    var sb = new StringBuilder();
    sb.AppendLine("BEGIN TRANSACTION;");
    
    // Generate SurrealQL from entity operations
    foreach (var op in entityOperations)
        op.WriteSurrealQL(sb);
    
    // Flush envelope operations into the same script
    foreach (var op in PendingOperations)
        await op(sb, ct);
    
    sb.AppendLine("COMMIT TRANSACTION;");
    await client.RawQuery(sb.ToString());
    PendingOperations.Clear();
}
```

This ensures domain data + envelope writes commit atomically in a single SurrealDB transaction.

### Registration

```csharp
// Minimal: Dali core + Wolverine persistence
builder.Services.AddDali(o => { /* ... */ });

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithDali();
    opts.Policies.AutoApplyTransactions();
});

// Or chained syntax (Marten-style):
builder.Services.AddDali(o => { /* ... */ })
    .IntegrateWithWolverine();
```

### Implementation Plan (4 Phases)

#### Phase 1: Foundation — DaliMessageStore + Transport (~22 files, ~15 tests)

| Step | File | Change |
|------|------|--------|
| 1 | `src/WolverineFx.Dali/WolverineFx.Dali.csproj` | New project: references `WolverineFx` + `Dali` |
| 2 | `src/WolverineFx.Dali/DaliMessageStore.cs` | `IMessageStore` + `IMessageInbox` + `IMessageOutbox` + `IDeadLetters` + `INodeAgentPersistence` + `IListenerStore` + `IMessageStoreAdmin` + `IScheduledMessages` + `DrainAsync()` + `ReassignIncomingAsync` |
| 3 | `src/WolverineFx.Dali/DaliEnvelope.cs` | Envelope SurrealDB record type (id, status, owner_id, execution_time, attempts, body, message_type) |
| 4 | `src/WolverineFx.Dali/DaliTransport.cs` | `ITransport` impl with `dali://` scheme |
| 5 | `src/WolverineFx.Dali/DaliQueueListener.cs` | Poll loop with `UPDATE ... RETURN BEFORE` CAS claim |
| 6 | `src/WolverineFx.Dali/DaliQueueSender.cs` | INSERT into outgoing table |
| 7 | `src/WolverineFx.Dali/DaliTransportOptions.cs` | Polling interval, live query toggle |
| 8 | `src/WolverineFx.Dali/DaliBackedPersistenceMarker.cs` | `IVariableSource` — tags handler chains with Dali persistence |
| 9 | `src/WolverineFx.Dali/DaliNullListenerStore.cs` | Null implementation for `IListenerStore` (accepts all registrations as no-ops) |
| 10 | `tests/WolverineFx.Dali.Tests/` | Tier 1 unit tests (in-memory engine) |

#### Phase 2: Core Change — Transactional Batching (2 files, ~8 tests)

| Step | File | Change |
|------|------|--------|
| 11 | `src/Dali/DocumentSession.cs` | Refactor `SaveChangesAsync` to batch all pending operations into single SurrealQL script wrapped in `BEGIN`/`COMMIT`; add `PendingOperations` queue |
| 12 | `src/Dali/SurrealQLBatchBuilder.cs` | Helper for building SurrealQL scripts from entity operations + envelope lambdas |
| 13 | `tests/Dali.Tests/SaveChangesTransactionTests.cs` | Tests: batch atomicity, rollback on error, envelope+domain same tx |

#### Phase 3: Outbox Integration (~7 files, ~8 tests)

| Step | File | Change |
|------|------|--------|
| 14 | `src/WolverineFx.Dali/DaliEnvelopeTransaction.cs` | `IEnvelopeTransaction` impl — queues envelope writes on DocumentSession.PendingOperations |
| 15 | `src/WolverineFx.Dali/FlushOutgoingMessagesOnDaliCommit.cs` | `IDocumentSessionListener` impl: BeforeSaveChangesAsync marks handled, AfterCommitAsync flushes |
| 16 | `src/WolverineFx.Dali/DaliOutboxedSessionFactory.cs` | Creates outbox-enrolled sessions |
| 17 | `src/WolverineFx.Dali/DaliIntegration.cs` | `IWolverineExtension` — registers codegen sources, policies, transport, saga config |
| 18 | `src/WolverineFx.Dali/WolverineOptionsDaliExtensions.cs` | `PersistMessagesWithDali()` + `IntegrateWithWolverine()` extensions |
| 19 | `src/WolverineFx.Dali/DaliSagaStorage.cs` | `ISagaStorage` impl via `IDocumentSession` |
| 20 | `tests/WolverineFx.Dali.Tests/` | Tier 2 integration tests (WolverineHost) |

#### Phase 4: Saga + Codegen + Hardening (~8 files, ~10 tests)

| Step | File | Change |
|------|------|--------|
| 21 | `src/WolverineFx.Dali/DaliPersistenceFrameProvider.cs` | `IPersistenceFrameProvider` — transaction support, saga frames |
| 22 | `src/WolverineFx.Dali/Codegen/OpenDaliSessionFrame.cs` | Codegen: opens outbox-enrolled `IDocumentSession` |
| 23 | `src/WolverineFx.Dali/Codegen/DaliSessionSaveChangesFrame.cs` | Codegen: `await session.SaveChangesAsync(ct)` |
| 24 | `src/WolverineFx.Dali/Codegen/FlushDaliOutgoingMessagesFrame.cs` | Codegen: `await ctx.FlushOutgoingMessagesAsync()` |
| 25 | `src/WolverineFx.Dali/Codegen/PrimeScopedDocumentSessionFrame.cs` | GH-3001 scope priming |
| 26 | `src/WolverineFx.Dali/ScopedDocumentSessionHolder.cs` | Holds enrolled session for DI resolution |
| 27 | `src/WolverineFx.Dali/DaliSagaStoreDiagnostics.cs` | `ISagaStoreDiagnostics` — saga explorer UI |
| 28 | `tests/WolverineFx.Dali.Tests/` | Tier 2–3 tests: saga lifecycle, codegen verification, multi-tenancy |

### Test Plan (3 tiers, 30+ tests)

#### Tier 1: DaliMessageStore Unit Tests (no Wolverine, in-memory SurrealDB)

| # | Test | What it verifies |
|---|------|-----------------|
| 1 | `Initialize_creates_envelope_tables` | Schema init creates all `wolverine_*` tables |
| 2 | `StoreIncoming_roundtrips` | Envelope inserted and can be polled |
| 3 | `StoreOutgoing_roundtrips` | Outgoing envelope persisted |
| 4 | `Claim_atomic_no_duplicates` | Two concurrent CAS claims don't duplicate |
| 5 | `MoveToDeadLetter_works` | Failed envelope moved to dead letter |
| 6 | `ScheduleExecution_works` | Execution time rescheduled |
| 7 | `ReassignIncoming_works` | Ownership reassigned for load balancing |
| 8 | `Binary_body_roundtrip` | `byte[]` body stored and retrieved correctly |
| 9 | `Binary_body_fallback_base64` | Base64 fallback if SDK binary path fails |
| 10 | `DrainAsync_completes_cleanly` | Graceful shutdown drains pending claims |
| 11 | `Node_persistence_election` | INodeAgentPersistence stores node record |
| 12 | `Listener_store_noop` | NullListenerStore accepts all registrations |
| 13 | `Scheduled_messages_roundtrip` | Delayed message stored with execution time |

#### Tier 2: Dali Core Transaction Batching Tests

| # | Test | What it verifies |
|---|------|-----------------|
| 14 | `Batch_commit_multiple_entities` | Multiple Store/Update/Delete in single tx |
| 15 | `Batch_rollback_on_error` | Error in one operation rolls back entire tx |
| 16 | `Batch_envelope_and_domain_same_tx` | Envelope writes + domain ops in single BEGIN/COMMIT |
| 17 | `Batch_preserves_operation_order` | Operations execute in session tracking order |

#### Tier 3: Wolverine Integration Tests (WolverineHost)

| # | Test | What it verifies |
|---|------|-----------------|
| 18 | `Transport_listener_polls_and_dispatches` | Queue listener delivers messages |
| 19 | `Transport_sender_delivers` | Outgoing envelope sent |
| 20 | `Tx_middleware_commits_both` | Handler + SaveChangesAsync commits both |
| 21 | `Tx_middleware_rolls_back_on_error` | Exception rolls back changes |
| 22 | `Outbox_flushes_after_commit` | Outgoing messages flushed post-commit |
| 23 | `Outbox_no_flush_on_rollback` | No flush if SaveChangesAsync fails |
| 24 | `Saga_create_and_load` | Saga state created and loaded |
| 25 | `Saga_update` | Saga state updated |
| 26 | `Saga_complete_and_delete` | Saga completed and removed |
| 27 | `Saga_concurrent_update_throws` | Optimistic concurrency on saga version |
| 28 | `End_to_end_handler_to_outbox` | Full handler → saga → outbox cycle |
| 29 | `Multi_tenant_roundtrips` | Tenant isolation with Dali + Wolverine |
| 30 | `Live_query_wakeup` | Live query triggers immediate poll |
| 31 | `Codegen_frames_generate_correctly` | Codegen frames produce valid IL |

### Risks (Updated per Council Review)

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Transactional SaveChanges** | ✅ Done | `SaveChangesAsync` uses `BeginTransaction()`/`Commit()`/`Cancel()`. Per-entity calls share transaction connection. |
| **IMessageStore contract larger than planned** | 🟡 P1 | 8 extra files (INodeAgentPersistence, IListenerStore, etc.). Accept scope increase — necessary for Wolverine correctness. |
| **CBOR binary serialization fails** | 🟡 P1 | Test byte[] round-trip before implementation. Fall back to Base64 in DaliMessageStore string body fields. |
| **RETURN BEFORE claim: MVCC vs Postgres SKIP LOCKED** | 🟡 P1 | CAS-based claim is safe for single-node. Add UNIQUE index. For distributed ≥100 msg/s, add partition key. |
| **No SQL-based migration infrastructure** | 🟢 P2 | SurrealDB has no `DbConnection`. `DaliMessageStore` self-owns schema via `DEFINE TABLE IF NOT EXISTS`. |
| **SurrealDB .NET SDK version must match** | 🟢 P2 | Dali pins SurrealDb.Net version. WolverineFx.Dali references Dali — version flows transitively. |
| **In-memory engine data loss on restart** | 🟢 P2 | Acceptable for testing. Production uses RocksDB or remote SurrealDB. |

### Bugs Fixed

| Bug | File | Fix |
|-----|------|-----|
| `FILTERS` keyword prepended per filter in `EnsureAnalyzerAsync` | `src/Dali/Schema/SchemaManager.cs:157` | Moved `FILTERS` before the comma-separated list instead of per-filter. Caused `Parse error: Unexpected token 'FILTERS'` in embedded engine. |

## ⚠️ Known Issues & Limitations

### 1. 🟠 CBOR Deserialization: Non-`Record` Types

**Problem:** The embedded engine uses Dahomey.Cbor for response deserialization. `GetValue<T>(0)` fails if `T` is not a `Record` subclass.

**Workaround — Two options:**

**Option A (preferred):** Extend `SurrealDb.Net.Models.Record`:
```csharp
public class MyEntity : SurrealDb.Net.Models.Record
{
    public string Name { get; set; }
}
```

**Option B (fallback for third-party types):** JSON round-trip:
```csharp
var raw = response.GetValue<List<object>>(0);
var json = JsonSerializer.Serialize(raw);
var result = JsonSerializer.Deserialize<T>(json);
```

**Option C (aggregate queries — e.g. `math::sum`):** Define a `Record` DTO with `[CborProperty]`:
```csharp
internal sealed class SumResult : SurrealDb.Net.Models.Record
{
    [CborProperty("math::sum")]
    public decimal Value { get; set; }
}
```

### 2. ✅ Include — Single Round Trip via LET Variables

`Include()` now uses SurrealDB's `LET` statement for server-side eager loading in
a **single round trip** — equivalent to Marten's PostgreSQL temp-table approach.

The generated SurrealQL caches the filtered main query in `$main`, then resolves
each Include via subqueries against the in-memory variable:

```surql
LET $main = (SELECT * FROM issue WHERE Status = 'Open');
SELECT * FROM $main;
SELECT * FROM user WHERE id IN (SELECT VALUE AssigneeId FROM $main);
SELECT * FROM project WHERE id IN (SELECT VALUE ProjectId FROM $main);
```

All statements execute in a single `RawQuery()`. The `SurrealDbResponse` is read at
indices 1 (main), 2+ (includes) — index 0 is the LET result.

**Tradeoff:** The LET variable stores full documents in memory. Marten's temp table
stores only IDs. For most workloads this is not a concern, but for 100K+ row
included sets, prefer `Fetch()` (inline expansion, zero intermediate storage).

### 3. 🟡 Live Queries: WebSocket Only

`WatchTableAsync<T>()` / `LiveTable<T>()` require a `ws://` or `wss://` connection. HTTP and embedded engines throw `NotSupportedException`.

### 4. 🟡 DB-per-Tenant: Advanced Client Bypass

`StoreOptions.Advanced.SurrealDbClient` returns the root client — tenant isolation only applies to store-created sessions.

### 5. 🔵 Pre-existing Build Warnings

| Warning | Status |
|---------|--------|
| `Aero.Cms.SourceGenerators` project reference warning | Non-blocking, pre-existing |
| `System.Threading.Channels` NU1510 pruning warning | Non-blocking, pre-existing |

---

## References

[^1]: Dave MacLeod, "New SurrealDB docs search using hybrid search and HNSW/BM25 reranking," SurrealDB Blog, Apr 2026. The SurrealDB documentation search engine uses BM25 full-text indexes with a custom analyzer (`search::score`), OpenAI `text-embedding-3-small` vector embeddings with HNSW indexes, and fuses both result sets via `search::rrf()` (Reciprocal Rank Fusion). [`Source`](https://surrealdb.com/blog/a-real-world-example-of-hybrid-fusion-search-using-the-surrealdb-docs-search)

[^2]: Dave MacLeod, "Visualising your data with Surrealist's Graph view," SurrealDB Blog, Mar 2025. Demonstrates graph relationships with RELATE, multi-hop traversals, recursive shortest-path queries, and the interactive Graph view in Surrealist. [`Source`](https://surrealdb.com/blog/visualising-your-data-with-surrealists-graph-view)
