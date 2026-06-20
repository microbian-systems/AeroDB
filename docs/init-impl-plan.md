# Dali — Implementation Plan

> MartenDB-style document database and event store on SurrealDB.

**Last modified:** 2026-06-20
**Version:** 0.1.0
**Build:** 0 errors (4 pre-existing stale warnings)
**Tests:** 144 passing, 0 failing, 0 skipped
**Projects:** `Dali`, `Dali.EntityFrameworkCore`, `Dali.Tests`

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
| ✚ Cross-cutting (logging, ConfigureAwait, ct) | 144 | — | ✅ Done |

---

## Phase 1: Core Document Store ✅

**Tests:** 8

| Layer | What's built |
|-------|-------------|
| Interfaces | `IDocumentStore`, `IQuerySession`, `IDocumentSession`, `IEvents` |
| Sessions | `QuerySession` (read-only), `DocumentSession` (read-write + unit of work) |
| LINQ | `SurrealExpressionVisitor` (Where, OrderBy, Skip, Take, Select, string methods), `SurrealQueryProvider` |
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
| Expression tree building | `ISurrealDbQueryable` aggregate methods now build `Expression.Call` trees for visitor dispatch |

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

## Backlog (Future)

| Area | Description | Priority |
|------|-------------|----------|
| **CI/CD** | GitHub Actions: build, test, package, publish NuGet | Medium |
| **Live projections** | SurrealDB `LIVE SELECT` → real-time projection updates | Medium |
| **Subscriptions** | Real-time event subscriptions via LIVE SELECT | Medium |
| **IDocumentListener** | Marten-style hooks (`BeforeSave`, `AfterSave`, etc.) | Small |
| **Batch operations** | Bulk insert/delete with chunked transactions | Small |
| **Full-text search** | Wrap SurrealDB's `search::*` functions | Medium |
| **CI/CD** | GitHub Actions: build, test, package, publish NuGet | Medium |

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

    // Tenancy
    o.TenancyStyle = TenancyStyle.Conjoined;
    o.DefaultTenantId = "default";
});
```

---

## Known Issues

| Issue | Workaround |
|-------|-----------|
| CBOR deserialization requires `Record` base class | Test models must extend `SurrealDb.Net.Models.Record` |
| EventRecord CBOR fetch fails with embedded engine | Use RawQuery + JSON round-trip for non-Record types |
| `Aero.Cms.SourceGenerators` project reference warning | Pre-existing repo issue, non-blocking |
| `System.Threading.Channels` NU1510 pruning warning | Pre-existing, non-blocking |
