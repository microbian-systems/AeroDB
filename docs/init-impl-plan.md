# Dali.SurrealDb.EfCore — Initial Implementation Plan

> Council-verified (2026-06-19). See `docs/efcore-plan.md` for the full architectural blueprint.

## Architecture Decisions

| Decision | Choice |
|----------|--------|
| Project count | **Single project** — everything in `Dali.SurrealDb.EfCore` |
| ID strategy | **Consumer's choice** — no Snowflake/Dali.Common dependency |
| SurrealDb.Net | **NuGet package v0.10.2** (submodule at `./surrealdb.net/` is for reference only) |
| EF Core base | **Non-relational** — `Microsoft.EntityFrameworkCore` only, NOT `Relational` |
| Package management | Central via `src/Directory.Packages.props` |
| Target framework | `net10.0` |

## NuGet Dependencies

### Core (add to `Directory.Packages.props`)

| Package | Version |
|---------|---------|
| `Microsoft.EntityFrameworkCore` | `10.0.8` |
| `Microsoft.EntityFrameworkCore.Abstractions` | `10.0.8` |
| `SurrealDb.Net` | `0.10.2` |

### Test

| Package | Version | Notes |
|---------|---------|-------|
| `TUnit` | `1.39.0` | Already in Packages.props |
| `Shouldly` | `4.3.0` | Already in Packages.props |
| `Microsoft.EntityFrameworkCore.Specification.Tests` | `10.0.8` | TBD — verify package name exists |

## Implementation Order (Council-Corrected)

```
  1. Project scaffolding + .csproj + Directory.Packages.props
  2. SurrealDbOptionsExtension + UseSurrealDb()
  3. SurrealDbDatabaseProvider (DI registration)
  4. SurrealDbTypeMappingSource (must precede conventions)
  5. Model conventions (RecordId PK, table naming, etc.)
  6. SurrealDbModelCacheKeyFactory (CRITICAL — DI fails without it)
  7. SurrealDbConnection (session lifecycle per DbContext)
  8. RecordId ↔ CLR PK bridge (ValueConverter)
  9. SurrealDbDatabaseCreator (DEFINE TABLE/FIELD)
 10. Minimal query + materialization (SELECT *)
 11. SaveChanges (Added→Create, Modified→Merge, Deleted→Delete)
 12. Transaction support
 13. WHERE clause + basic query translation
 14. EF Core spec tests
 15. Migrations (last — depends on everything above)
```

## Classes to Create

### Layer 1: Provider Registration & DI

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbOptionsExtension` | `IDbContextOptionsExtension` | `SurrealDbOptionsExtension.cs` |
| `SurrealDbDatabaseProvider` | `IDatabaseProvider` | `SurrealDbDatabaseProvider.cs` |
| `SurrealDbOptionsBuilderExtensions` | (static extensions) | `SurrealDbOptionsBuilderExtensions.cs` |
| `ServiceCollectionExtensions` | (static extensions) | `ServiceCollectionExtensions.cs` |

### Layer 2: Model & Metadata

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbTypeMappingSource` | `ITypeMappingSource` | `SurrealDbTypeMappingSource.cs` |
| `SurrealDbTypeMapping` | `CoreTypeMapping` | `SurrealDbTypeMapping.cs` |
| `SurrealDbModelValidator` | `IModelValidator` | `SurrealDbModelValidator.cs` |
| `SurrealDbModelFinalizedConvention` | `IModelFinalizingConvention` | `SurrealDbModelFinalizedConvention.cs` |
| `SurrealDbValueGeneratorSelector` | `IValueGeneratorSelector` | `SurrealDbValueGeneratorSelector.cs` |
| `SurrealDbModelCacheKeyFactory` | `IModelCacheKeyFactory` | `SurrealDbModelCacheKeyFactory.cs` |

### Infrastructure

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbConnection` | (session lifecycle) | `SurrealDbConnection.cs` |

### Layer 3: Query Pipeline

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbQueryCompiler` | `IQueryCompiler` | `SurrealDbQueryCompiler.cs` |
| `SurrealDbQueryContextFactory` | `IQueryContextFactory` | `SurrealDbQueryContextFactory.cs` |
| `SurrealDbShapedQueryCompilingExpressionVisitor` | `IShapedQueryCompilingExpressionVisitor` | `SurrealDbShapedQueryCompilingExpressionVisitor.cs` |
| `SurrealDbQueryableMethodTranslatingExpressionVisitor` | (ExpressionVisitor) | `SurrealDbQueryableMethodTranslatingExpressionVisitor.cs` |
| `SurrealDbBinaryExpressionTranslator` | (ExpressionVisitor) | `SurrealDbBinaryExpressionTranslator.cs` |
| `SurrealDbMemberTranslator` | (ExpressionVisitor) | `SurrealDbMemberTranslator.cs` |
| `SurrealDbQueryExpressionFactory` | (factory) | `SurrealDbQueryExpressionFactory.cs` |

### Layer 4: Storage & Change Tracking

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbDatabase` | `IDatabase`, `IDatabaseAsync` | `SurrealDbDatabase.cs` |
| `SurrealDbTransactionManager` | `IDbContextTransactionManager` | `SurrealDbTransactionManager.cs` |
| `SurrealDbTransaction` | `IDbContextTransaction` | `SurrealDbTransaction.cs` |
| `SurrealDbExecutionStrategy` | `IExecutionStrategy` | `SurrealDbExecutionStrategy.cs` |
| `SurrealDbDatabaseCreator` | `IDatabaseCreator` | `SurrealDbDatabaseCreator.cs` |
| `SurrealDbCommandBatchPreparer` | (batch grouping) | `SurrealDbCommandBatchPreparer.cs` |

### Layer 5: Migrations (Design-Time)

| Class | Implements | File |
|-------|-----------|------|
| `SurrealDbMigrationSqlGenerator` | `IMigrationsSqlGenerator` | `SurrealDbMigrationSqlGenerator.cs` |
| `SurrealDbDatabaseModelFactory` | `IDatabaseModelFactory` | `SurrealDbDatabaseModelFactory.cs` |
| `SurrealDbHistoryRepository` | `IHistoryRepository` | `SurrealDbHistoryRepository.cs` |
| `SurrealDbMigrationsAssembly` | `IMigrationsAssembly` | `SurrealDbMigrationsAssembly.cs` |
| `SurrealDbDesignTimeServices` | (DI registration) | `SurrealDbDesignTimeServices.cs` |

## Old Files to Remove

| File | Action |
|------|--------|
| `SurrealModels.cs` | Delete |
| `SurrealContext.cs` | Delete |
| `QueryProvider.cs` | Delete |
| `ExpressionVisitor.cs` | Delete |
| `Extensions.cs` | Delete |
| `Examples.cs` | Keep (excluded from build, reference material) |

## Key Patterns

### Session Lifecycle (per Council recommendation)

```
DbContext (scoped)
  ├── ISurrealDbClient (from DI, singleton)
  ├── SurrealDbConnection (scoped, creates ISurrealDbSession via client.CreateSession())
  └── All CRUD → SurrealDbConnection.Session (not client directly)
```

### RecordId PK Bridge

- CLR entity exposes `long Id` or `string Id` (consumer's choice)
- Internal storage uses `RecordIdOf<long>` or `RecordIdOf<string>`
- `ValueConverter` handles the dual representation transparently
- `SurrealDbModelFinalizedConvention` applies the PK convention automatically

### Non-Relational EF Core Pattern

- Extends `Microsoft.EntityFrameworkCore` directly (like Cosmos DB provider)
- Does NOT reference `Microsoft.EntityFrameworkCore.Relational`
- `IQueryCompiler` is required for all `DbSet<T>` operations — even simple `ToListAsync()`

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| `IModelCacheKeyFactory` missing → runtime crash | Added to class list, implemented in Step 6 |
| RecordId bridging underspecified | Dedicated ValueConverter design before coding |
| Options extension caching bugs | Reference Cosmos DB provider source |
| `IShapedQueryCompilingExpressionVisitor` complexity | Iterative approach; test materialization early |
| SurrealDb.Net v0.10.2 API drift | Pin exact version; test against submodule |
| Spec test package may not exist | Prepare to write own test suite if needed |
