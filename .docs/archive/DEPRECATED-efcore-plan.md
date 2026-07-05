# [DEPRECATED] Dali.SurrealDb.EfCore — Architectural Plan

> ⚠️ **DEPRECATED (2026-06-20)** — This plan has been superseded by the standalone Dali library (Marten-style document DB + event store on SurrealDB).
>
> See [architecture.md](architecture.md) and [init-impl-plan.md](init-impl-plan.md) for the current documentation.
>
> Kept as reference for future EF Core provider work.

## Overview

A **non-relational** EF Core database provider for SurrealDB (v3.x), built on top of `surrealdb.net`. Pattern-aligned with the Cosmos DB provider (direct `Microsoft.EntityFrameworkCore`, not `Microsoft.EntityFrameworkCore.Relational`). Event-sourcing layer (MartenDB parity) deferred to a follow-up module.

**Core first-class features:** Transactions (SurrealDB v3 session-based), Migrations (design-time + runtime schema evolution), full LINQ → SurrealQL translation, and change tracking.

## Package Structure

```
Dali.SurrealDb.EfCore/                                 # Core provider
  Dali.SurrealDb.EfCore.csproj
Dali.SurrealDb.EfCore.Design/                          # Design-time services (migrations)
  Dali.SurrealDb.EfCore.Design.csproj
Dali.SurrealDb.EventSourcing/                          # Event-sourcing module (future)
  Dali.SurrealDb.EventSourcing.csproj
Dali.SurrealDb.EventSourcing.EntityFrameworkCore/      # EF projection storage (future)
  Dali.SurrealDb.EventSourcing.EntityFrameworkCore.csproj
Dali.SurrealDb.EfCore.Tests/                           # Functional + spec tests
  Dali.SurrealDb.EfCore.Tests.csproj
```

**Naming convention:** `Dali.SurrealDb.EfCore` (not `SurrealDb.EntityFrameworkCore`) to align with the `Dali` project namespace and avoid conflicts with the upstream `surrealdb.net` packages.

## Target Dependencies

- `Microsoft.EntityFrameworkCore` (non-relational — no `Relational` package)
- `Microsoft.EntityFrameworkCore.Abstractions`
- `Microsoft.Extensions.DependencyInjection`
- `SurrealDb.Net` (submodule at `./surrealdb.net/` — transport, auth, connection management)
- `Dali.Common` (Snowflake ID generation, shared `IEntity<long>` / `Entity`)

## Data Transport & Architecture Reference

The `surrealdb.net` submodule provides:

| Capability | API |
|------------|-----|
| CRUD | `Create<T>`, `Select<T>`, `Update<T>`, `Delete<T>`, `Upsert<T>`, `Patch<T>`, `Insert<T>`, `Merge<T>`, `Relate` |
| Raw queries | `RawQuery(sql, params)`, `Query(interpolatedHandler)` |
| Sessions (v3+) | `CreateSession()`, `CloseSession()`, `ForkSession()` |
| Transactions (v3+) | `BeginTransaction()`, `Commit()`, `Cancel()` |
| Auth | `SignIn(RootAuth/NamespaceAuth/DatabaseAuth/ScopeAuth)`, `SignUp`, `Authenticate` |
| Transport | CBOR over HTTP/WS, engine strategy (`ISurrealDbEngine`) |

## Transactions

SurrealDB v3.x provides native session-based transactions via `ISurrealDbSession.BeginTransaction()` / `Commit()` / `Cancel()`. These map directly to EF Core's `IDbContextTransaction` and are a **first-class feature from Phase 1**, not deferred.

### Transaction Architecture

```
┌──────────────────────────────────────────────────────────┐
│  DbContext.Database.BeginTransaction()                    │
│  → SurrealDbTransactionManager.StartTransaction()         │
├──────────────────────────────────────────────────────────┤
│  SurrealDbTransactionManager                              │
│  ┌──────────────────────────────────────────────────────┐ │
│  │ SurrealDbSession -> BeginTransaction()               │ │
│  │ → returns SurrealDbTransaction (wraps ISurrealDbTx)  │ │
│  │ → stores current tx on ambient session               │ │
│  └──────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────┤
│  SaveChanges within transaction:                          │
│  ┌──────────────────────────────────────────────────────┐ │
│  │ All CRUD operations execute on the same session+tx   │ │
│  │ ● Commit  → Transaction is committed to SurrealDB    │ │
│  │ ● Rollback → Transaction is cancelled                │ │
│  │ ● Dispose → Auto-rollback if uncommitted             │ │
│  └──────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────┤
│  Nested / ambient transaction support:                    │
│  ┌──────────────────────────────────────────────────────┐ │
│  │ SurrealDbTransaction.Enlist(ISurrealDbEngine)        │ │
│  │ → enlists all operations in the same SurrealDB tx    │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### Key classes

- `SurrealDbTransactionManager` — implements `IDbContextTransactionManager`. Wraps `ISurrealDbSession` transaction lifecycle.
- `SurrealDbTransaction` — implements `IDbContextTransaction`. Proxies to `SurrealDbTransaction.Commit()` / `Cancel()`.
- `SurrealDbDatabaseFacadeExtensions` — `UseSurrealDb()` extension wires transaction manager into EF Core DI.

### Transaction boundary rules

1. Each `SaveChangesAsync` call is **auto-enlisted** in the ambient transaction if one is active.
2. If no ambient transaction exists, each `SaveChangesAsync` executes **without** an explicit SurrealDB transaction (each operation is its own implicit unit). The `ExecutionStrategy` handles retries at this level.
3. When `Database.AutoTransactionBehavior = Always` (or similar EF Core config), the provider wraps every `SaveChangesAsync` in a `BeginTransaction`/`Commit` pair automatically.

---

## Migrations

Migrations are supported as a first-class feature through the `Dali.SurrealDb.EfCore.Design` package, enabling the full `dotnet ef migrations` workflow.

### Migration Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  dotnet ef migrations add InitialCreate                       │
│  → Reads EF Core model snapshot                               │
│  → Calls SurrealDbMigrationSqlGenerator                        │
│  → Produces C# migration class with Up() / Down() methods     │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbMigrationSqlGenerator                                │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ Model change → SurrealQL statements:                      │ │
│  │ ● CreateTable   → DEFINE TABLE ... SCHEMAFULL            │ │
│  │ ● AddColumn     → DEFINE FIELD ... ON TABLE ... TYPE ... │ │
│  │ ● DropColumn    → REMOVE FIELD ... ON TABLE ...           │ │
│  │ ● CreateIndex   → DEFINE INDEX ... ON TABLE ... COLUMNS  │ │
│  │ ● DropIndex     → REMOVE INDEX ... ON TABLE ...          │ │
│  │ ● DropTable     → REMOVE TABLE ...                       │ │
│  │ ● AlterColumn   → DEFINE FIELD ... (overwrite)           │ │
│  └──────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbDatabaseModelFactory                                │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ Reverse-engineers database into EF Core model:            │ │
│  │ ● INFO FOR DB → list tables                               │ │
│  │ ● INFO FOR TABLE {name} → field definitions, indexes     │ │
│  │ ● Builds IModel programmatically                          │ │
│  └──────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbHistoryRepository                                   │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ Stores migration history in `_ef_migrations_history`      │ │
│  │ DEFINE TABLE _ef_migrations_history SCHEMAFULL            │ │
│  │ Fields: migration_id (string), product_version (string)   │ │
│  └──────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbDatabaseCreator                                     │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ EnsureCreated() / EnsureDeleted():                        │ │
│  │ ● EnsureDeleted → REMOVE TABLE for each entity           │ │
│  │ ● EnsureCreated → DEFINE TABLE/FIELD/INDEX from model    │ │
│  │ ● HasPendingModelChanges() → compare model vs database   │ │
│  └──────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

### Design-time workflow

```
dotnet ef migrations add InitialCreate
  → generates {timestamp}_InitialCreate.cs in Migrations/
  → Up() contains SurrealQL migration statements
  → Down() contains inverse statements

dotnet ef database update
  → applies pending migrations in order
  → SurrealDbHistoryRepository tracks applied migrations

dotnet ef database update {migration}
  → rolls forward or backward to target migration

dotnet ef migrations remove
  → removes last migration (if unapplied)

dotnet ef dbcontext scaffold
  → reverse-engineers model from existing SurrealDB database
  → SurrealDbDatabaseModelFactory reads INFO FOR DB / INFO FOR TABLE
```

### Key classes

- `SurrealDbMigrationSqlGenerator` — implements `IMigrationsSqlGenerator`. Translates migration operations to SurrealQL.
- `SurrealDbDatabaseModelFactory` — implements `IDatabaseModelFactory`. Reverse-engineers database schema.
- `SurrealDbHistoryRepository` — implements `IHistoryRepository`. Tracks migration history in SurrealDB.
- `SurrealDbMigrationsAssembly` — implements `IMigrationsAssembly`. Discovers migration classes.
- `SurrealDbDatabaseCreator` — implements `IDatabaseCreator`. Schema-level operations (EnsureCreated, EnsureDeleted).
- `SurrealDbDesignTimeServices` — design-time DI registration for the above services (in `Dali.SurrealDb.EfCore.Design`).

---

## Tooling Reference

- **MCP SurrealDB Server** — linked but requires agent session restart to activate. After reboot, provides direct schema exploration and live query testing against target SurrealDB instances.
- **`surrealdb.net` submodule** — at `./surrealdb.net/`. The sole transport dependency. No additional transport layer should be added.
- **Marten source reference** — at `./marten/`. Pattern reference for event-sourcing architecture in future phases.

## Architecture Layers

### Layer 1: Provider Registration & DI

```
┌──────────────────────────────────────────────────────────┐
│  DbContextOptionsBuilder.UseSurrealDb(endpoint, config)   │
│  via SurrealDbOptionsExtension                            │
├──────────────────────────────────────────────────────────┤
│  SurrealDbDatabaseProvider (implements IDatabaseProvider) │
│  Registers all internal services in DI container          │
└──────────────────────────────────────────────────────────┘
```

**Key classes:**

- `SurrealDbOptionsExtension` — carries endpoint, namespace, database, auth, and config. Implements `IDbContextOptionsExtension`.
- `SurrealDbDatabaseProvider` — `IDatabaseProvider` singleton. Maps SurrealDB as a named provider.
- `ServiceCollectionExtensions.AddEntityFrameworkSurrealDb()` — extension for `IServiceCollection`, registers all internal services. Used internally by `UseSurrealDb()`.

### Layer 2: Model & Metadata

```
┌──────────────────────────────────────────────────────────────┐
│  SurrealDbModelValidator                                      │
│  Validates: RecordId PK, no unsupported types, navigation     │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbModelFinalizedConvention                            │
│  Applies: RecordId as PK, Snowflake value gen, table naming   │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbTypeMappingSource                                   │
│  CLR → SurrealDB type mapping (string→string, int→int, etc.)  │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbValueGeneratorSelector                              │
│  Long PK → SnowflakeValueGenerator (Dali's Snowflake.NewId()) │
└──────────────────────────────────────────────────────────────┘
```

**EF Core entity → SurrealDB schema mapping:**

| EF Core Concept | SurrealDB Mapping |
|----------------|-------------------|
| `DbSet<T>` / entity type | `DEFINE TABLE {type.Name} SCHEMAFULL` |
| Scalar property | `DEFINE FIELD {prop} ON TABLE {table} TYPE {stype}` |
| Primary key | `RecordIdOf<long>` (Snowflake) or `RecordIdOf<string>` (UUID). Handled by internal convention — not an explicit field, but the implicit `id`. |
| Index | `DEFINE INDEX {name} ON TABLE {table} COLUMNS {fields} [UNIQUE]` |
| Navigation (→ single) | `RecordId` reference field + `FETCH` |
| Navigation (→ collection) | SurrealDB graph relation via `In`/`Out` convention |
| Owned types / embedded | `FLEXIBLE` object fields or nested `DEFINE TABLE` |
| Inheritance / TPH | Discriminator field |
| Concurrency token | Version field compared in `WHERE` clause on `UPDATE` / `DELETE` |

**RecordId key handling:**
- The CLR entity exposes `long Id` (Snowflake) or `string Id` (UUID/Guid) per `IEntity<long>`.
- The internal storage creates `RecordIdOf<long>` or `RecordIdOf<string>` wrapping the Id.
- The serializer handles the dual representation: client sees `long`/`string`, SurrealDB sees `RecordId(table:id)`.

### Layer 3: Query Pipeline

```
┌──────────────────────────────────────────────────────────────┐
│  LINQ Expression Tree                                        │
│  db.Blogs.Where(b => b.Rating > 3).OrderBy(b => b.Name)      │
└───────────────────────┬──────────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────────┐
│  SurrealDbQueryCompiler                                      │
│  ExpressionVisitor that builds SurrealQL AST                 │
├──────────────────────────────────────────────────────────────┤
│  Translators (visitor pattern):                               │
│  ┌──────────────────────────────────────────────────────────┐ │
│  │ Where      → WHERE clause builder                       │ │
│  │ OrderBy    → ORDER BY clause builder                    │ │
│  │ Skip       → START clause builder                       │ │
│  │ Take       → LIMIT clause builder                       │ │
│  │ Select     → Field projection builder                   │ │
│  │ Include    → FETCH / subquery resolver                  │ │
│  │ Any/All/Contains → subquery with conditions             │ │
│  │ String ops → string::* SurrealQL functions              │ │
│  │ Math ops   → math::* SurrealQL functions / operators    │ │
│  │ Aggregations → GROUP BY + aggregate functions           │ │
│  │ Count      → count()                                    │ │
│  │ First/Single → LIMIT 1 / LIMIT 2                       │ │
│  │ Concat/Union → subquery unions                          │ │
│  └──────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────┤
│  Result: SurrealQL query string + parameter dict              │
│  e.g. "SELECT rating, name FROM blog WHERE rating > $p0       │
│        ORDER BY name ASC LIMIT 10"                           │
│  params: { "p0": 3 }                                         │
└──────────────────────────────────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────────┐
│  SurrealDbQueryContextFactory                                 │
│  Wraps QueryContext around the current DbContext + session    │
├──────────────────────────────────────────────────────────────┤
│  SurrealDbDatabase.ExecuteSqlQueryAsync                       │
│  → ISurrealDbClient.RawQuery(sql, params)                     │
└──────────────────────────────────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────────┐
│  SurrealDbShapedQueryCompilingExpressionVisitor               │
│  Materializes raw result rows → entity instances              │
│  Handles: RecordId deserialization, Duration conversion,      │
│  relation hydration (FETCH results), owned types              │
└──────────────────────────────────────────────────────────────┘
```

**Key classes:**

- `SurrealDbQueryCompiler` — implements `IQueryCompiler`. Entry point for expression tree → SurrealQL translation.
- `SurrealDbQueryableMethodTranslatingExpressionVisitor` — LINQ method call → SurrealQL construct translation.
- `SurrealDbBinaryExpressionTranslator` — binary operations (==, >, <, &&, `||`) → SurrealQL operators.
- `SurrealDbMemberTranslator` — member access → field name translation (handles navigation property dereferencing).
- `SurrealDbQueryContextFactory` — implements `IQueryContextFactory`.
- `SurrealDbShapedQueryCompilingExpressionVisitor` — implements `IShapedQueryCompilingExpressionVisitor`. Maps result columns back to entity shapes.
- `SurrealDbQueryExpressionFactory` — builds parameterized SurrealQL strings from the compiled expression tree.

**Translation examples:**

| LINQ | SurrealQL |
|------|-----------|
| `.Where(b => b.Rating > 3)` | `WHERE rating > $p0` |
| `.OrderBy(b => b.Name)` | `ORDER BY name ASC` |
| `.Skip(10).Take(5)` | `START 10 LIMIT 5` |
| `.Select(b => new { b.Name })` | `SELECT name FROM ...` |
| `.Include(b => b.Posts)` | Use `FETCH posts` with relation field, or subquery: `SELECT *, (SELECT * FROM post WHERE blog = $parent.id) AS posts FROM blog` |
| `.Count()` | `SELECT count() FROM ... GROUP BY ALL` |
| `.FirstOrDefault(b => b.Id == 1)` | `SELECT * FROM ... WHERE id = $p0 LIMIT 1` |
| `.Any(b => b.Rating > 3)` | `SELECT * FROM ... WHERE rating > $p0 LIMIT 1` |

### Layer 4: Storage & Change Tracking

```
┌──────────────────────────────────────────────────────────────┐
│  DbContext.SaveChangesAsync()                                 │
│  → ChangeTracker collects Added / Modified / Deleted entities │
└───────────────────────┬──────────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────────┐
│  SurrealDbDatabase.SaveChangesAsync()                         │
│  Wraps ISurrealDbClient methods per entity state:             │
│  ┌──────────────────────────────────────────────────────────┐│
│  │ Added    → ISurrealDbClient.Create(table, entity)        ││
│  │ Modified → ISurrealDbClient.Merge(recordId, entity)      ││
│  │ Deleted  → ISurrealDbClient.Delete(recordId)             ││
│  │ Added (relation) → ISurrealDbClient.Relate(in, out, data)││
│  └──────────────────────────────────────────────────────────┘│
├──────────────────────────────────────────────────────────────┤
│  Transaction support (v3):                                    │
│  ┌──────────────────────────────────────────────────────────┐│
│  │ SurrealDbSession.BeginTransaction()                      ││
│  │ → execute all operations in the transaction              ││
│  │ → Commit() or Cancel()                                    ││
│  └──────────────────────────────────────────────────────────┘│
├──────────────────────────────────────────────────────────────┤
│  Concurrency handling:                                        │
│  ┌──────────────────────────────────────────────────────────┐│
│  │ Record version field with optimistic concurrency checks   ││
│  │ Compare version in WHERE clause on UPDATE / DELETE        ││
│  └──────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────┘
```

**Key classes:**

- `SurrealDbDatabase` — implements `IDatabase`, `IDatabaseAsync`. Wraps `ISurrealDbClient` for all data operations.
- `SurrealDbTransactionManager` — manages session/transaction lifecycle for SaveChanges.
- `SurrealDbExecutionStrategy` — implements `IExecutionStrategy`. Retry logic for transient HTTP failures.
- `SurrealDbDatabaseCreator` — implements `IDatabaseCreator`. Ensures schema exists (DEFINE TABLE/FIELD/INDEX).
- `SurrealDbCommandBatchPreparer` — groups pending changes into batch operations.

### Layer 5: Schema Management (Design-Time)

- `SurrealDbMigrationSqlGenerator` — generates SurrealQL from EF Core model snapshots (DEFINE TABLE, DEFINE FIELD, DEFINE INDEX).
- `SurrealDbDatabaseModelFactory` — reverse-engineers a database model from existing SurrealDB schema (via `INFO FOR TABLE`, `INFO FOR DB`).
- `SurrealDbMigrationsRepository` — stores migration history in a `_migrations` table.
- `SurrealDbHistoryRepository` — implements `IHistoryRepository`.

### Layer 6: Type Mapping Detail

| CLR Type | SurrealDB Type | Notes |
|----------|---------------|-------|
| `long`, `int`, `short`, `byte` | `int` | Snowflake for PKs |
| `float`, `double`, `decimal` | `float` | |
| `string` | `string` | |
| `bool` | `bool` | |
| `DateTime`, `DateTimeOffset` | `datetime` | |
| `TimeSpan` | `duration` | Uses `SurrealDb.Net.Models.Duration` internally |
| `Guid` | `string` | As UUID formatted string, or native UUID |
| `byte[]` | `bytes` | |
| `object` / `JsonDocument` | `object` | FLEXIBLE mode fields |
| `Dictionary<string, object>` | `object` | FLEXIBLE |
| `T[]`, `List<T>`, `IEnumerable<T>` | `array` | |
| SurrealDb.Net spatial types | `geometry` | `Microsoft.Spatial` types |
| `RecordIdOf<T>` | implicit record `id` | Handled at the storage layer, not exposed in queries |
| `None` / nullable | `option<T>` | Nullable wrapper |
| JSON / POCO objects | `object` | FLEXIBLE, or recursive DEFINE FIELD |

## Event-Sourcing Module (Future Phase — Deferred)

Architecturally planned but deferred from initial implementation:

```
Dali.SurrealDb.EventSourcing/
  SurrealDbEventStore.cs               -> IEventStore implementation
  SurrealDbEventStore.Append.cs        -> Append / AppendAsync
  SurrealDbEventStore.Streams.cs       -> StartStream, FetchStream
  Events/
    SurrealDbEventMapper.cs            -> CLR event type ↔ SurrealDB event record
    EventMetadata.cs                   -> Metadata config (correlation, causation, headers)
    Schema/
      DefineEventsTable.cs             -> DEFINE TABLE mt_events SCHEMAFULL
      DefineStreamsTable.cs            -> DEFINE TABLE mt_streams SCHEMAFULL
      DefineEventProgressionTable.cs   -> DEFINE TABLE for async daemon progress
  Projections/
    SurrealDbProjectionStorage.cs      -> IProjectionStorage<TDoc, TId> adapter
    SurrealDbSingleStreamProjection.cs -> Aggregate projection base (single stream)
    SurrealDbMultiStreamProjection.cs  -> Aggregate projection base (multi-stream)
    SurrealDbEventProjection.cs        -> Event-by-event projection base
    SurrealDbProjectionExtensions.cs   -> Registration extensions
  Daemon/
    SurrealDbAsyncDaemon.cs            -> Async projection daemon
    SurrealDbHighWaterDetector.cs      -> High-water mark tracking
    SurrealDbEventLoader.cs            -> Event range loading with type filters
  MultiTenancy/
    SurrealDbTenancy.cs                -> Conjoined tenant_id on event tables
    SurrealDbDatabasePerTenant.cs      -> Separate client per tenant

Dali.SurrealDb.EventSourcing.EntityFrameworkCore/
  EfCoreProjectionStorage.cs           -> IProjectionStorage using EF Core DbContext
  EfCoreSingleStreamProjection.cs      -> Aggregate projection persisted via EF Core
  EfCoreMultiStreamProjection.cs       -> Multi-stream aggregate via EF Core
  EfCoreEventProjection.cs             -> Event projection via EF Core
  TransactionParticipant.cs            -> Bridges SurrealDB transaction into DbContext
  SurrealDbEfCoreProjectionExtensions.cs -> StoreOptions.Add<...>() extensions
```

## MartenDB Parity Comparison

| Feature | MartenDB (PG) | Dali.SurrealDb.EfCore (Phase 1) | Event Sourcing (Future) |
|---------|--------------|----------------------------------|------------------------|
| Provider Base | Relational (Npgsql) | Non-relational (EF Core) | N/A |
| Query Translation | LINQ → SQL via EF Core | LINQ → SurrealQL via custom compiler | N/A |
| Storage | JSONB document tables | SurrealDB document store (native) | Event streams table |
| Schema Management | Weasel migrations | DEFINE TABLE / FIELD / INDEX | DEFINE TABLE + projections |
| Identity | GUID / String per config | Snowflake (long) or GUID | Event GUID + stream version |
| Change Tracking | Snapshot diff dirty checking | EF Core change tracker → CRUD | N/A |
| Transactions | NpgsqlTransaction | SurrealDB v3 session / transaction | Transaction participant bridge |
| Concurrency | Optimistic via version | Optimistic via version | Expected stream version |
| Multi-tenancy | DB-per-tenant + conjoined | Conjoined tenant_id field | Conjoined + per-database |
| Projections | Inline + Async Daemon | N/A | EventProjection, Single/MultiStream |
| Async Daemon | Full async projection pipeline | N/A | High-water mark + event loader |
| Schema Creation | Auto-create via Weasel | DEFINE via database creator | N/A |
| Spec Tests | `EFCore.Relational.Specification.Tests` | `EFCore.Specification.Tests` (non-relational) | N/A |
| Transport | ADO.NET binary protocol | HTTP CBOR / WebSocket | Same as core |

## Key SurrealQL Patterns

### CRUD Operations from EF Core

**SELECT (list):**
```surql
SELECT * FROM blog WHERE rating > 3 ORDER BY name ASC LIMIT 10 START 0
```

**SELECT (single by id):**
```surql
SELECT * FROM blog WHERE id = blog:1234
```

**CREATE:**
```surql
CREATE blog CONTENT { title: 'Hello', rating: 5, author: person:5678 }
```

**UPDATE (merge):**
```surql
UPDATE blog:1234 MERGE { rating: 4 }
```

**DELETE:**
```surql
DELETE blog:1234
```

**DELETE (by query):**
```surql
DELETE (SELECT id FROM blog WHERE rating < 2)
```

**ExecuteUpdate pattern (subquery method for performance):**
```surql
UPDATE (SELECT id FROM blog WHERE rating < 2) SET rating = 0
```

**RELATE (for graph edge creation):**
```surql
RELATE person:5678->wrote->blog:1234 CONTENT { role: 'author' }
```

**FETCH (eager loading):**
```surql
SELECT * FROM blog FETCH author
```

## Implementation Phases

### Phase 1: Minimal Viable Provider

1. Project scaffolding — `Dali.SurrealDb.EfCore.csproj` with EF Core + SurrealDb.Net deps
2. `SurrealDbOptionsExtension` + `UseSurrealDb()` extension method
3. `SurrealDbDatabaseProvider` — DI service registration
4. Basic model conventions — RecordId PK, Snowflake value gen, table naming
5. `SurrealDbTypeMappingSource` — CLR → SurrealDB type mapping
6. `SurrealDbDatabaseCreator` — DEFINE TABLE / FIELD from EF Core model
7. Simple `DbSet<T>` materialization (`SELECT * FROM table`)
8. SaveChanges: Added → Create, Modified → Merge, Deleted → Delete
9. Transaction support — `SurrealDbTransactionManager`, `SurrealDbTransaction` wrapping `ISurrealDbSession.BeginTransaction()` / `Commit()` / `Cancel()`, auto-enlistment in `SaveChangesAsync`
10. Migration support:
    - `SurrealDbMigrationSqlGenerator` — DEFINE/REMOVE TABLE/FIELD/INDEX statement generation
    - `SurrealDbHistoryRepository` — `_ef_migrations_history` table in SurrealDB
    - `SurrealDbDatabaseModelFactory` — `INFO FOR DB` / `INFO FOR TABLE` reverse engineering
    - `SurrealDbDesignTimeServices` — DI wiring in `Dali.SurrealDb.EfCore.Design`
11. Basic query: `Where` clause translation (==, >, <, &&, `||`)
12. EF Core specification tests (non-relational suite: `Microsoft.EntityFrameworkCore.Specification.Tests`)

### Phase 2: Full Query Translation

1. Complete LINQ method translators — OrderBy, Skip, Take, First, Single, Count, Any, All, Contains
2. Projection support — Select with anonymous types and member init
3. String / math / date function translations to SurrealQL `string::*`, `math::*`, `time::*`
4. Aggregate operations — Sum, Min, Max, Average
5. Include / ThenInclude — FETCH clause and subquery resolution
6. Relationship navigation — graph relations via `IRelationRecord` pattern

### Phase 3: Advanced Features

1. Optimistic concurrency — version field tracking with conditional updates in WHERE clause
2. Execution strategy — retry logic for transient HTTP / WS failures
3. Batch operations — `ExecuteDelete`, `ExecuteUpdate` via SurrealQL subquery pattern
4. Raw SQL queries via `FromSqlRaw` — pass-through to `RawQuery`

### Phase 4: Event Sourcing (Future)

1. Event / stream storage schema (DEFINE TABLE for mt_events, mt_streams)
2. Append / StartStream operations on top of ISurrealDbClient
3. Single-stream aggregate projection base class
4. Multi-stream aggregate projection base class
5. Inline projection lifecycle (runs within SaveChanges)
6. Async daemon — high-water mark, event loader, shard coordination
7. EF Core projection storage adapter (`EfCoreProjectionStorage`)
8. Specification tests for event sourcing module

## Key Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| SurrealQL LINQ translation is complex; SurrealQL may not express all SQL patterns | Start with the 80% case; use client-side evaluation fallback. Iteratively expand translator coverage |
| HTTP transport latency vs ADO.NET binary protocol | Batch operations within a single `RawQuery` call where possible. SurrealDB's ability to run multi-statement SurrealQL scripts helps |
| No SurrealDB-specific EF Core specification test suite exists | Use the non-relational `Microsoft.EntityFrameworkCore.Specification.Tests` package; adapt Cosmos DB test patterns |
| Transaction support (v3) may have limitations until server API stabilizes | Fall back to single-operation mode for unsupported scenarios |
| RecordId dual-representation (typed vs string) adds complexity | Keep a clear internal `RecordIdHelper` abstraction that isolates serialization logic |

## EF Core Services to Implement

The following internal EF Core service interfaces must be implemented for a non-relational provider:

| Service Interface | Implementation |
|-------------------|----------------|
| `IDatabaseProvider` | `SurrealDbDatabaseProvider` |
| `IDatabase` | `SurrealDbDatabase` |
| `IQueryCompiler` | `SurrealDbQueryCompiler` |
| `IQueryContextFactory` | `SurrealDbQueryContextFactory` |
| `IShapedQueryCompilingExpressionVisitor` | `SurrealDbShapedQueryCompilingExpressionVisitor` |
| `ITypeMappingSource` | `SurrealDbTypeMappingSource` |
| `IModelValidator` | `SurrealDbModelValidator` |
| `IModelFinalizingConvention` | `SurrealDbModelFinalizedConvention` |
| `IValueGeneratorSelector` | `SurrealDbValueGeneratorSelector` |
| `ITransactionManager` | `SurrealDbTransactionManager` |
| `IDbContextTransactionManager` | `SurrealDbTransactionManager` |
| `IExecutionStrategy` | `SurrealDbExecutionStrategy` |
| `IMigrationsSqlGenerator` | `SurrealDbMigrationSqlGenerator` |
| `IDatabaseModelFactory` | `SurrealDbDatabaseModelFactory` |
| `IHistoryRepository` | `SurrealDbHistoryRepository` |
| `IMigrationsAssembly` | `SurrealDbMigrationsAssembly` |
| `IDatabaseCreator` | `SurrealDbDatabaseCreator` |
