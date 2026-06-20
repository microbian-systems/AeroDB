# Project Overview

## Docs

| File | Content |
|------|---------|
| `efcore-plan.md` | **Architectural plan** for the SurrealDB EF Core provider — a non-relational provider built on `surrealdb.net`. Covers 6 layers (DI, model/metadata, query pipeline, storage/change tracking, schema management, type mapping), transactions (v3 session-based), migrations (DEFINE TABLE/FIELD/INDEX via SurrealQL), and 4 implementation phases. Event-sourcing module is deferred to future. |
| `marten-llms-full.txt` | MartenDB reference docs covering advanced SQL querying, aggregate projections (single/multi-stream), conventions, explicit code, and Wolverine integration. |
| `wovlerine-llms-full.txt` | WolverineFx reference covering ancillary stores (Marten + Polecat), aggregate handler workflow with event sourcing, `[WriteAggregate]`/`[ReadAggregate]`, DCB pattern, natural keys, strong-typed IDs. |

## Project Structure

| File | Purpose |
|------|---------|
| `SurrealModels.cs` | `RecordId`, `SurrealEntity<T>`, `SurrealEdge<TIn,TOut>`, domain models |
| `ExpressionVisitor.cs` | LINQ expression tree → SurrealQL string translation |
| `QueryProvider.cs` | `IQueryProvider`, `SurrealQueryable<T>`, `SurrealDbSet<T>`, async helpers |
| `SurrealContext.cs` | DbContext equivalent, `SurrealGraphApi`, HTTP executor |
| `Extensions.cs` | `ToListAsync`, `Traverse<>`, `Raw()`, DI registration |
| `Examples.cs` | Usage examples for document/graph/SQL modes |

## Key Observations

- The current codebase is an **exploratory/prototype** — a standalone LINQ provider, **not** a true EF Core provider implementing `IDatabaseProvider`, `IQueryCompiler`, etc.
- `tests/` and `build/` directories are empty.
- No `Dali.SurrealDb.EfCore.Design`, `.EventSourcing`, or `.Tests` projects exist yet.
- The plan describes a full EF Core provider with migrations, transactions, change tracking, and event sourcing — all still to be built.
- The `surrealdb.net` submodule provides transport (CRUD, sessions, transactions, raw queries).
- The `wolverine/` and `marten/` submodules serve as source references for future event-sourcing architecture.
