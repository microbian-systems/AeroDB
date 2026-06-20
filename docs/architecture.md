# Dali.SurrealDb — Marten-style Document Database & Event Store for SurrealDB

## Vision

A pragmatic .NET document database and event store on SurrealDB — inspired by MartenDB's architecture on PostgreSQL. This is **not** an EF Core provider; it's a standalone library with its own LINQ provider, session management, change tracking, and event sourcing, built directly on `SurrealDb.Net`.

> **Tagline:** .NET Transactional Document DB and Event Store on SurrealDB. Pragmatic library to boost your developer productivity!

## Why Not EF Core?

EF Core's non-relational provider API requires access to internal types (`IUpdateEntry`, `IDbContextServices`, etc.) that are only available to providers in the EF Core repository. Third-party providers must use `Microsoft.EntityFrameworkCore.Relational`, which is designed for SQL databases. Building a relational provider on top of SurrealQL adds unnecessary abstraction.

Instead, we follow Marten's proven approach:
- Direct SurrealDB connectivity via `SurrealDb.Net`
- Own LINQ provider translating to SurrealQL
- Own session/document model
- Full event sourcing with projections

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                         User Code                             │
│  IDocumentStore ──→ IDocumentSession / IQuerySession          │
│       ↓                          ↓                            │
│  DocumentStore ──→ DocumentSession / QuerySession             │
│       ↓                          ↓                            │
│  Schema: DEFINE TABLE/FIELD/INDEX     LINQ → SurrealQL        │
│  Migration: compare model vs DB       Identity Map            │
│  Multi-tenancy                        Change Tracking          │
│       ↓                          ↓                            │
│  ┌────────────────────────────────────────────────────────┐   │
│  │              SurrealDb.Net (ISurrealDbClient)           │   │
│  │        CRUD · RawQuery · Sessions · Transactions        │   │
│  └────────────────────────────────────────────────────────┘   │
│       ↓                                                        │
│  ┌────────────────────────────────────────────────────────┐   │
│  │              SurrealDB Server (HTTP/WS)                 │   │
│  │        Documents · Graph · Events · Live Queries        │   │
│  └────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│                  Event Sourcing Module                        │
│  ┌──────────┐  ┌──────────────┐  ┌────────────────────────┐ │
│  │ Event     │  │  Projections  │  │  Async Daemon          │ │
│  │ Store     │  │  · Inline     │  │  · High-water mark     │ │
│  │ · Append  │  │  · Live       │  │  · Event Loader        │ │
│  │ · Streams │  │  · Async      │  │  · Shard coordination  │ │
│  │ · Fetch   │  │  · Multi/Single Stream │                 │ │
│  └──────────┘  └──────────────┘  └────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

## Session Model (Marten-aligned)

| Session Type | Change Tracking | Use Case |
|-------------|----------------|----------|
| `IQuerySession` | None (read-only) | Queries, reporting |
| `IDocumentSession` | Identity map | Writes with concurrency |
| `IDocumentSession` (Dirty) | Snapshot diff | Writes with dirty checking |

## Document Store

```csharp
// Configuration
var store = DocumentStore.For(options =>
{
    options.Connection(endpoint, ns, db, user, pass);
    options.Schema.For<Person>()
        .IndexOn(p => p.Email)
        .UniqueIndex(p => p.Id);
});

// Read-only session
await using var query = store.QuerySession();
var adults = await query.Query<Person>()
    .Where(p => p.Age >= 18)
    .OrderBy(p => p.Name)
    .ToListAsync();

// Read-write session with identity map
await using var session = store.LightweightSession();
var person = new Person { Name = "Alice", Age = 30 };
session.Store(person);
session.Store(new Person { Name = "Bob", Age = 25 });
await session.SaveChangesAsync();
```

## Event Sourcing

```csharp
// Append events
var stream = await session.Events.StartStream<Order>(
    new OrderCreated { OrderId = 1 },
    new ItemAdded { Sku = "ABC", Qty = 2 }).ConfigureAwait(false);

// Fetch stream
var events = await session.Events.FetchStream(stream.Id);

// Projections
store.Events.Projections.Add<OrderProjection>(ProjectionLifecycle.Inline);
```

## LINQ → SurrealQL Translation

| LINQ | SurrealQL |
|------|-----------|
| `.Where(p => p.Age > 3)` | `WHERE age > $p0` |
| `.OrderBy(p => p.Name)` | `ORDER BY name ASC` |
| `.Skip(10).Take(5)` | `START 10 LIMIT 5` |
| `.Select(p => new { p.Name })` | `SELECT name FROM ...` |
| `.Count()` | `SELECT count() FROM ... GROUP BY ALL` |
| `p.Tags.Contains("x")` | `tags CONTAINS 'x'` |
| `p.Name.Contains("foo")` | `string::contains(name, 'foo')` |

## Project Structure

```
src/
  Dali.SurrealDb/                          # Core library
    Dali.SurrealDb.csproj
    DocumentStore.cs                        # IDocumentStore implementation
    DocumentSession.cs                      # IDocumentSession (identity map)
    QuerySession.cs                         # IQuerySession (read-only)
    LightweightSession.cs                   # IDocumentSession (no identity map)
    Storage/                                # Document CRUD
    Linq/                                   # LINQ → SurrealQL provider
    Schema/                                 # Schema management (DEFINE TABLE/FIELD/INDEX)
    Events/                                 # Event sourcing
      EventStore.cs
      StreamAction.cs
      EventGraph.cs
    Projections/                            # Projection support
    Daemon/                                 # Async projection daemon
    MultiTenancy/                           # Multi-tenancy support
    Serialization/                          # JSON/RecordId serialization
  Dali.SurrealDb.Tests/                     # Tests
```

## Implementation Phases

### Phase 1: Core Document Store (Current)
1. `IDocumentStore` / `DocumentStore` — configuration, schema, connection
2. `IQuerySession` / `QuerySession` — read-only queries + LINQ
3. `ILightweightSession` / `LightweightSession` — writes without identity map
4. `IDocumentSession` / `DocumentSession` — identity map + change tracking
5. LINQ → SurrealQL provider (expression visitor)
6. Schema management (DEFINE TABLE/FIELD/INDEX)
7. Multi-tenancy basics

### Phase 2: Event Sourcing
1. Event store: append, fetch streams
2. Inline projections
3. Live projections (via SurrealDB live queries)
4. Async projection daemon

### Phase 3: Advanced Features
1. Batch operations (ExecuteDelete, ExecuteUpdate)
2. Compiled queries
3. Patch / partial update API
4. Optimistic concurrency via version fields
5. Soft delete

## References

- MartenDB source: `./marten/`
- WolverineFx source: `./wolverine/`
- SurrealDb.Net source: `./surrealdb.net/`
- [Marten docs](./docs/marten-llms-full.txt)
- [Wolverine docs](./docs/wovlerine-llms-full.txt)
