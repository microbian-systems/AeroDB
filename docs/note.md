# Project Overview

## Direction Change (2026-06-19)

**Previous plan:** Build a non-relational EF Core provider for SurrealDB
**Current plan:** Build **Dali.SurrealDb** — a MartenDB-inspired document database and event store on SurrealDB

This is a standalone library (NOT an EF Core provider) with its own LINQ provider, session management, change tracking, and event sourcing, built directly on `SurrealDb.Net`. EF Core integration can come later.

### Why the change?

EF Core's non-relational provider API requires internal types (`IUpdateEntry`, `IDbContextServices`, etc.) that are only accessible to providers in the EF Core repo. Third-party providers must use `Microsoft.EntityFrameworkCore.Relational`, which is designed for SQL databases. Building a relational provider abstraction over SurrealQL adds unnecessary complexity — follow Marten's proven approach instead.

## Docs

| File | Content |
|------|---------|
| `architecture.md` | **Current** — Marten-style document DB + event store architecture |
| `efcore-plan.md` | **Superseded** — kept for future EF Core provider reference |
| `marten-llms-full.txt` | MartenDB reference docs (document storage, LINQ, projections, etc.) |
| `wovlerine-llms-full.txt` | WolverineFx reference (event sourcing patterns) |

## Project Structure

```
src/
  Dali.SurrealDb/                        # Core library (Marten-style)
    Dali.SurrealDb.csproj
    IDocumentStore.cs                    # Core interfaces
    DocumentStore.cs                     # Store implementation
    QuerySession.cs / DocumentSession.cs # Session implementations
    Linq/                                # LINQ → SurrealQL provider
    Storage/                             # Document CRUD operations
    Schema/                              # DEFINE TABLE/FIELD/INDEX
    Events/                              # Event sourcing
    Projections/                         # Projection support
    Serialization/                       # JSON/RecordId handling

surrealdb.net/                           # Submodule: SurrealDb.Net transport
marten/                                  # Submodule: Marten reference
wolverine/                               # Submodule: WolverineFx reference
```

## Current Status

- Phase 1 (Core Document Store) — **in progress**
- Phase 2 (Event Sourcing) — **planned**
- Phase 3 (Advanced Features) — **planned**
