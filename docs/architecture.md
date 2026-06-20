# Dali — Marten-style Document Database & Event Store for SurrealDB

> .NET Transactional Document DB and Event Store on SurrealDB. Built directly on `SurrealDb.Net` — not an EF Core provider.

**Last modified:** 2026-06-20
**Full plan:** [init-impl-plan.md](init-impl-plan.md)
**Build:** 0 errors (4 pre-existing warnings)
**Tests:** 81 passing

---

## Vision

A pragmatic .NET document database and event store on SurrealDB — inspired by MartenDB's architecture on PostgreSQL. This is a standalone library with its own LINQ provider, session management, change tracking, and event sourcing.

## Why Not EF Core?

EF Core's non-relational provider API requires access to internal types (`IUpdateEntry`, `IDbContextServices`, etc.) that are only available to providers in the EF Core repository. Third-party providers must use `Microsoft.EntityFrameworkCore.Relational`, which is designed for SQL databases. Building a relational provider on top of SurrealQL adds unnecessary abstraction.

Instead, we follow Marten's proven approach:
- Direct SurrealDB connectivity via `SurrealDb.Net` 0.10.2
- Own LINQ provider translating to SurrealQL
- Own session/document model
- Full event sourcing with projections
- Optional EF Core transaction bridge (`Dali.EntityFrameworkCore`)

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                         User Code                                 │
│  IDocumentStore ──→ IDocumentSession / IQuerySession              │
│       ↓                              ↓                            │
│  DocumentStore ──→ DocumentSession / QuerySession                 │
│       ↓                              ↓                            │
│  Schema Manager      LINQ → SurrealQL      Tenancy Filter         │
│  (DEFINE TABLE/FIELD/INDEX)                (Conjoined/Per-DB)     │
│       ↓                              ↓                            │
│  ┌────────────────────────────────────────────────────────────┐   │
│  │              SurrealDb.Net (ISurrealDbClient)               │   │
│  │        CRUD · RawQuery · Sessions · Transactions            │   │
│  └────────────────────────────────────────────────────────────┘   │
│       ↓                                                            │
│  ┌────────────────────────────────────────────────────────────┐   │
│  │              SurrealDB Server (Embedded/HTTP/WS)             │   │
│  └────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                     Event Sourcing Module                         │
│  ┌──────────┐  ┌──────────────┐  ┌────────────────────────────┐  │
│  │ Event     │  │  Projections  │  │  Async Daemon             │  │
│  │ Store     │  │  · Inline     │  │  · High-water mark        │  │
│  │ · Append  │  │  · Async      │  │  · Event polling          │  │
│  │ · Streams │  │  · Single     │  │  · Background worker      │  │
│  │ · Fetch   │  │  · Multi      │  │  · ILogger<T>             │  │
│  └──────────┘  └──────────────┘  └────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

## Session Model

| Session Type | Change Tracking | Use Case |
|-------------|----------------|----------|
| `IQuerySession` | None (read-only) | Queries, reporting |
| `IDocumentSession` (Lightweight) | Unit of Work | Writes without identity map |
| `IDocumentSession` (Dirty) | Snapshot diff | Writes with dirty checking |

## Quick Start

```csharp
// Configuration
var store = Documents.For(o =>
{
    o.ClientFactory = () => new SurrealDbMemoryClient();  // or Endpoint for remote
    o.Namespace = "myapp";
    o.Database = "mydb";
    o.Events.Enabled = true;
    o.LoggerFactory = loggerFactory;
});

// Read-only session
await using var query = store.QuerySession();
var adults = await query.Query<Person>()
    .Where(p => p.Age >= 18)
    .OrderBy(p => p.Name)
    .ToListAsync();

// Read-write session
await using var session = store.LightweightSession();
session.Store(new Person { Name = "Alice", Age = 30 });
await session.SaveChangesAsync();
```

## LINQ → SurrealQL Translation

| LINQ | SurrealQL |
|------|-----------|
| `.Where(p => p.Age > 3)` | `WHERE Age > 3` |
| `.Where(p => p.Name == "Alice" && p.Age > 20)` | `WHERE (Name = 'Alice') AND (Age > 20)` |
| `.OrderBy(p => p.Name).ThenByDescending(p => p.Age)` | `ORDER BY Name ASC, Age DESC` |
| `.Skip(10).Take(5)` | `START 10 LIMIT 5` |
| `.Select(p => new PersonDto { Name = p.Name })` | `SELECT Name AS Name FROM person` |
| `.CountAsync()` | `SELECT * FROM person` (client-side count due to CBOR) |
| `.SumAsync(p => p.Price)` | `SELECT * FROM product` (client-side sum) |
| `.AnyAsync()` | `SELECT * FROM person LIMIT 1` |
| `.Where(p => p.Email.Contains("@test"))` | `WHERE string::contains(Email, '@test')` |
| `.Where(p => p.CreatedAt.Year == 2024)` | `WHERE time::year(CreatedAt) = 2024` |
| `.FirstOrDefaultAsync()` | `SELECT * FROM person LIMIT 1` |
| `.SingleOrDefaultAsync()` | `SELECT * FROM person LIMIT 2` (throws if >1) |

> **Note:** Field names use PascalCase (C# property names) to match SurrealDB's CBOR serialization. Table names use snake_case.

## Event Sourcing

```csharp
// Append events
var streamId = await session.Events.StartStream("order-1",
    new OrderCreated { OrderId = 1 });

// Fetch stream
var events = await session.Events.FetchStream("order-1");

// Inline projection
store.Options.Projections.Add(new OrderProjection());
```

## Tenancy

```csharp
// Conjoined tenancy (tenant_id on every document)
o.TenancyStyle = TenancyStyle.Conjoined;
o.DefaultTenantId = "tenant-a";

// All queries auto-filter: WHERE TenantId = 'tenant-a'
// All stores auto-set TenantId on entities with the property
```

## EF Core Bridge

The optional `Dali.EntityFrameworkCore` package coordinates transactions between Dali and EF Core:

```csharp
services.AddDaliWithEfCore<MyDbContext>(o =>
{
    o.Endpoint = "http://localhost:8000";
    o.Namespace = "myapp";
    o.Database = "mydb";
});

// In DbContext.OnConfiguring:
optionsBuilder.UseDaliTransactionManager<MyDbContext>(store);
```

## Logging

`ILogger<T>` is used throughout. Log levels:
- **Information:** Store init, session creation, SaveChanges batch commit, projection cycles, daemon start/stop
- **Debug:** Generated SurrealQL, entity CRUD operations, tenant filter application, schema operations

Configure via `StoreOptions`:
```csharp
o.LoggerFactory = loggerFactory;
o.MinimumLogLevel = LogLevel.Debug;
```

If `LoggerFactory` is null, `NullLogger<T>` is used everywhere (no-op).

## Project Structure

```
src/
  Dali/                                    # Core library
    Dali.csproj
    DocumentStore.cs                       # IDocumentStore implementation
    DocumentSession.cs                     # IDocumentSession (unit of work)
    QuerySession.cs                        # IQuerySession (read-only)
    InternalSessionBase.cs                 # Shared session base
    StoreOptions.cs                        # Configuration
    ISurrealDbQueryable.cs                 # Queryable types
    SurrealAsyncQueryExtensions.cs         # Async query helpers
    Storage/
      DocumentStorage.cs                   # Table name mapping
    Linq/
      ExpressionVisitor.cs                 # LINQ → SurrealQL visitor
      SurrealQueryProvider.cs              # IQueryProvider implementation
    Events/
      IEvents.cs                           # Event store interface
      EventStore.cs                        # Append, FetchStream, StartStream
    Projections/
      IProjection.cs / IProjectionContext.cs
      InlineProjection.cs / SingleStreamProjection.cs / MultiStreamProjection.cs
      AsyncDaemon.cs                       # Background polling daemon
    Schema/
      SchemaManager.cs                     # DEFINE TABLE/FIELD/INDEX
    MultiTenancy/                          # Tenancy support (conjoined, per-db)

  Dali.EntityFrameworkCore/                # EF Core bridge (optional)
    Dali.EntityFrameworkCore.csproj
    DaliEfCoreTransaction.cs              # Shared transaction wrapper
    DaliEfCoreTransactionManager.cs       # IDbContextTransactionManager
    ServiceCollectionExtensions.cs        # DI registration

  Dali.slnx                                # Solution file

tests/
  Dali.Tests/                              # TUnit + Shouldly tests
```

## Progress

All 6 implementation phases are complete. See [init-impl-plan.md](init-impl-plan.md) for the full plan, test counts, and backlog.

| Phase | Tests | Status |
|-------|-------|--------|
| 1. Core Document Store | 8 | ✅ |
| 2. Test Coverage | 30 | ✅ |
| 3. Projections | 37 | ✅ |
| 4. Multi-Tenancy + Schema | 49 | ✅ |
| 5. Query Translation | 64 | ✅ |
| 6. EF Core Bridge | 81 | ✅ |

## Constraints & Conventions

- **Serialization:** SurrealDb CBOR via `Record` base class. Use `GetValue<List<T>>(0)` for Record types, `GetValue<List<object>>(0)` + JSON round-trip for non-Record types.
- **Field names:** PascalCase (C# property names) to match CBOR. Table names: snake_case.
- **Async:** `ConfigureAwait(false)` on all SurrealDb.Net calls. `CancellationToken` forwarded through all async methods.
- **Test framework:** TUnit + Shouldly. No FluentAssertions, Moq, XUnit, NUnit, MSTest.
- **Models:** Extend `SurrealDb.Net.Models.Record` for CBOR compatibility.
- **Dependencies:** SurrealDb.Net 0.10.2 (Dali), Microsoft.EntityFrameworkCore 10.0.8 (Dali.EntityFrameworkCore).

## References

- MartenDB source: `./marten/`
- WolverineFx source: `./wolverine/`
- SurrealDb.Net source: `./surrealdb.net/`
