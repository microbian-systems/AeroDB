# Dali — Implementation Plan

> MartenDB-style document database and event store on SurrealDB. Council-verified architecture (2026-06-19).

## Current State

- **Namespace:** `Dali`
- **Project:** `src/Dali/Dali.csproj` → `Dali.dll`
- **Tests:** `tests/Dali.Tests/Dali.Tests.csproj` (TUnit + Shouldly, embedded SurrealDB)
- **Dependency:** `SurrealDb.Net` 0.10.2 (NuGet)

## Phase 1: Core Document Store ✅

**Status:** Complete (8 tests passing)

| Layer | What's built |
|-------|-------------|
| Interfaces | `IDocumentStore`, `IQuerySession`, `IDocumentSession`, `IEvents` |
| Sessions | `QuerySession` (read-only), `DocumentSession` (read-write + unit of work) |
| LINQ | `SurrealExpressionVisitor` (Where, OrderBy, Skip, Take, Select, string methods, Tags), `SurrealQueryProvider` |
| Storage | `DocumentStorage` (snake_case table naming), `UnitOfWork` (Added/Modified/Deleted) |
| Events | `EventStore` (Append, StartStream, FetchStream on `mt_events`) |
| Schema | `SchemaManager` (DEFINE TABLE/FIELD/INDEX) |
| DI | `Documents.For()` factory, `StoreOptions.ClientFactory` for embedded clients |

**Tests (8):** Store/query/delete, Query all + Take, Event append/fetch empty

## Phase 2: More Test Coverage (Current)

**Priority:** Highest — users ordered 3 first

| Area | Tests to add |
|------|-------------|
| Query translation | OrderBy/OrderByDescending, Compound WHERE (AND/OR), FirstOrDefault, Select projection, Skip, Contains, Count |
| Sessions | Store + Load by Id, Delete + verify deleted, Dirty tracking update, Identity map across multiple operations |
| Unit of work | Mixed operations (add + modify + delete in one SaveChanges), Batch size validation, Error rollback |
| Edge cases | Query empty set, Query null result, Duplicate key behavior, Large batch inserts |
| Event store | Fetch after append with named types, Multiple streams isolation, Version ordering |

**Test models** must extend `SurrealDb.Net.Models.Record` for CBOR compatibility.

## Phase 3: Projections

| Area | What to build |
|------|--------------|
| Inline projections | `IProjection` interface, `ProjectionLifecycle.Inline`, runs within SaveChanges |
| Live projections | SurrealDB live queries via `LiveTable<T>()` |
| Async daemon | `AsyncDaemon` class with high-water mark, `EventLoader`, shard coordination |
| Projection storage | `IProjectionStorage` interface, `InlineProjection<TDoc>`, `AsyncProjection<TDoc>` |
| Single-stream aggregate | `SingleStreamProjection<T>` base class |
| Multi-stream aggregate | `MultiStreamProjection<T>` base class |

## Phase 4: Multi-Tenancy + Schema

| Area | What to build |
|------|--------------|
| Conjoined tenancy | `tenant_id` field on all documents, automatic WHERE filter |
| Database-per-tenant | `ISurrealDbClient` per tenant, `TenantDatabaseSelector` |
| Schema auto-create | `SchemaManager.EnsureDocumentSchemaAsync()` on store init |
| Schema diff | `SchemaManager.HasPendingChanges()` — compare model vs DB |

## Phase 5: Query Translation Improvements

| Feature | LINQ → SurrealQL |
|---------|-----------------|
| Full OrderBy | OrderBy, ThenBy, OrderByDescending, ThenByDescending |
| Compound conditions | AND (`&&`), OR (`\|\|`), NOT, nested parens |
| Select projection | Anonymous types → named columns, MemberInit |
| Aggregates | Sum, Min, Max, Average, Count |
| String functions | Contains → `string::contains`, StartsWith, EndsWith |
| Math functions | `math::*` translation for +, -, *, / |
| Date functions | `time::*` translations |
| Include/FETCH | Eager loading via FETCH clause |
| First/Single | `LIMIT 1` / `LIMIT 2` with validation |

## Phase 6: EF Core Bridge

| Area | What to build |
|------|--------------|
| Transaction participant | `DbContextTransactionParticipant<TDbContext>` — bridges SurrealDB tx into EF Core DbContext |
| Session adapter | `EfCoreOperations<TDbContext>` — simultaneous Dali + EF Core writes |
| DI registration | `AddDaliWithEfCore()` extension |

## Known Issues

| Issue | Workaround |
|-------|-----------|
| CBOR deserialization requires `Record` base class | Test models must extend `SurrealDb.Net.Models.Record` |
| EventRecord CBOR fetch fails with embedded engine | Use RawQuery + JSON round-trip for non-Record types |
| `Aero.Cms.SourceGenerators` project reference warning | Pre-existing repo issue, non-blocking |
