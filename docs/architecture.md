# Dali — Marten-style Document Database & Event Store for SurrealDB

> .NET Transactional Document DB and Event Store on SurrealDB. Built directly on `SurrealDb.Net` — not an EF Core provider.

**Last modified:** 2026-06-20
**Full plan:** [init-impl-plan.md](init-impl-plan.md)
**Build:** 0 errors (4 pre-existing warnings)
**Tests:** 144 passing

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

### SurrealDB Search & Vector Functions

SurrealDB provides a rich set of built-in search and vector functions for hybrid (full-text + vector) retrieval, as demonstrated in the SurrealDB docs search engine[^1]. These are mapped to LINQ or exposed as raw SurrealQL via `Session.RawQuery`:

| Function | Purpose | LINQ / API | Notes |
|----------|---------|-----------|-------|
| `search::analyze(analyzer, text)` | Tokenize + stem text with a named analyzer | Raw SurrealQL | E.g. `search::analyze("simple", "The project lead frowned...")` |
| `search::score(n)` | BM25 relevance score for `@n@` match operator | Raw SurrealQL (planned) | Used within `WHERE field @0@ $query` clauses |
| `search::rrf(array, k, limit)` | Reciprocal Rank Fusion — combines ranked lists | Raw SurrealQL (planned) | `search::rrf([$ft, $vs], 60, 80)` fuses full-text + vector results |
| `vector::distance::knn()` | KNN distance inside `<\|>\|` operator scope | Raw SurrealQL | `SELECT ..., vector::distance::knn() AS distance FROM page WHERE embedding <\|30,100\|> $qvec` |
| `vector::similarity::cosine(a, b)` | Cosine similarity between two vectors | Raw SurrealQL | Brute-force; HNSW index preferred for scale[^1] |

**Full-text search** requires a `FULLTEXT ANALYZER` index with BM25 configuration:
```sql
DEFINE ANALYZER simple TOKENIZERS blank, class, camel, punct FILTERS SNOWBALL(en);
DEFINE INDEX ft ON page FIELDS title FULLTEXT ANALYZER simple BM25(1.2, 0.75);
```

**Vector search** uses an `HNSW` index for approximate nearest-neighbor:
```sql
DEFINE INDEX page_embedding_hnsw ON page FIELDS embedding HNSW DIMENSION 1536 DIST COSINE;
```

**Hybrid fusion** combines both with RRF:
```sql
LET $fused = search::rrf([$fulltext_results, $vector_results], 60, 80);
```

> See the backlog for planned LINQ integration of these functions. Currently accessible via `Session.RawQuery`.

[^1]: Dave MacLeod, "New SurrealDB docs search using hybrid search and HNSW/BM25 reranking," SurrealDB Blog, Apr 2026. The doc search engine uses `search::rrf()` to fuse BM25 full-text scores with OpenAI embedding vector results, applying `search::score()` weighting per field. [`Source`](https://surrealdb.com/blog/a-real-world-example-of-hybrid-fusion-search-using-the-surrealdb-docs-search)

## Schema Modes (SCHEMAFULL vs SCHEMALESS)

SurrealDB supports two schema modes per table, configurable via `DocumentMapping<T>.SetSchemaMode()`:

| Mode | SurrealQL | Behavior |
|------|-----------|----------|
| `SchemaMode.Strict` | `DEFINE TABLE ... SCHEMAFULL` | Only explicitly defined fields are permitted; extra fields are rejected (default). |
| `SchemaMode.Flexible` | `DEFINE TABLE ... SCHEMALESS` | Fields are typed/validated if defined, but extra fields are allowed. |

**Default is `SchemaMode.Strict`** (SCHEMAFULL), matching traditional SQL expectations. Switch to `Flexible` for schemas where document shapes may vary:

```csharp
var store = Documents.For(o =>
{
    o.Schema.For<Person>()
        .SetSchemaMode(SchemaMode.Flexible)  // allow extra fields
        .Index(p => p.Email);
});
```

The schema mode is applied during `DocumentStore.InitializeAsync` via the `SchemaManager`.

## Raw SQL / SurrealQL Queries

Dali exposes `RawQueryAsync<T>()` and `ExecuteSqlAsync()` on all session types (`IQuerySession`, `IDocumentSession`) for direct SurrealQL execution:

```csharp
await using var session = store.QuerySession();

// Raw query returning typed results
var results = await session.RawQueryAsync<Person>(
    "SELECT * FROM person WHERE age > $minAge",
    new Dictionary<string, object?> { ["minAge"] = 18 });

// Execute non-query statements (CREATE, UPDATE, DELETE, DEFINE)
await session.ExecuteSqlAsync("CREATE person CONTENT { name: 'Alice', age: 30 }");
```

These methods delegate to the underlying `ISurrealDbSession.RawQuery()` from `surrealdb.net`. Parameters use named `$param` placeholders with a dictionary — safe from injection.

## Native Event Triggers (DEFINE EVENT)

SurrealDB provides server-side event triggers that fire automatically on `CREATE`, `UPDATE`, and `DELETE` operations. These are **distinct** from Dali's Marten-style event sourcing — they are database-level triggers defined with `DEFINE EVENT`.

### SurrealQL Reference

```surql
-- Single event type
DEFINE EVENT user_created ON TABLE user
  WHEN $event = "CREATE"
  THEN ( CREATE audit SET event = $event, table_name = "user", record_id = $after.id );

-- Combined event types with before/after state
DEFINE EVENT user_changes ON TABLE user
  WHEN $event = "CREATE" OR $event = "UPDATE" OR $event = "DELETE"
  THEN ( CREATE audit SET event = $event, before = $before, after = $after );

-- Async execution with retries
DEFINE EVENT slow_job ON TABLE publication
  WHEN $event = "CREATE"
  ASYNC RETRY 3 MAXDEPTH 5
  THEN ( ... );
```

Special variables available in the `THEN` block:

| Variable | Description |
|----------|-------------|
| `$event` | The operation type: `"CREATE"`, `"UPDATE"`, or `"DELETE"` |
| `$before` | The record state before the change (null on CREATE) |
| `$after` | The record state after the change (null on DELETE) |
| `$this` | The current record |

Events are visible via `INFO FOR TABLE {name} → events`.

### C# Configuration

```csharp
var store = Documents.For(o =>
{
    o.Events.Triggers.AutoCreateTriggers = true;
    o.Events.Triggers.AddTrigger(
        name: "user_created",
        table: "user",
        action: "CREATE audit SET event = $event, table_name = 'user', record_id = $after.id",
        whenCondition: "$event = 'CREATE'"
    );
    o.Events.Triggers.AddTrigger(
        name: "user_changes",
        table: "user",
        action: "CREATE audit SET event = $event, before = $before, after = $after",
        whenCondition: "$event = 'CREATE' OR $event = 'UPDATE' OR $event = 'DELETE'"
    );
});
```

Triggers are applied during `DocumentStore.InitializeAsync`. Use `EventTriggerManager` for runtime management:

```csharp
var triggerManager = new EventTriggerManager(loggerFactory);
await triggerManager.EnsureTriggerAsync(session, triggerDef);
await triggerManager.AlterTriggerAsync(session, triggerDef);
await triggerManager.RemoveTriggerAsync(session, name, table);
```

## User-Defined Functions (DEFINE FUNCTION)

SurrealDB supports user-defined functions written in SurrealQL, callable from any query. They are scoped to the database and visible via `INFO FOR DB → functions`.

### SurrealQL Reference

```surql
-- Expression body
DEFINE FUNCTION fn::greet($name: string) {
    RETURN "Hello, " + $name;
};

-- Block body with logic
DEFINE FUNCTION fn::math::double($n: int) {
    RETURN $n * 2;
};

-- Usage
RETURN fn::greet("World");   -- "Hello, World"
RETURN fn::math::double(21); -- 42
```

### C# Configuration

```csharp
var store = Documents.For(o =>
{
    o.Functions.AutoCreateFunctions = true;
    o.Functions.Register(
        name: "fn::greet",
        body: "RETURN 'Hello, ' + $name;",
        parameters: "$name: string"
    );
    o.Functions.Register(
        name: "fn::math::double",
        body: "RETURN $n * 2;",
        parameters: "$n: int"
    );
});
```

Functions are created during `DocumentStore.InitializeAsync`. Use `FunctionManager` for runtime management:

```csharp
var functionManager = new FunctionManager(loggerFactory);
await functionManager.EnsureFunctionAsync(session, function);
await functionManager.RemoveFunctionAsync(session, "fn::greet");
```

## Graph Capabilities (Planned — Phase 16)

SurrealDB has first-class graph relationships as native records. The built-in Graph view in Surrealist provides a visual representation of these relationships, turning `SELECT` queries with graph paths into interactive node-edge diagrams[^2]. Dali will expose a dedicated `IGraphQuery<T>` API separate from the document LINQ provider.

### Type Model

```csharp
// Base for all edge types — SurrealDB edges ARE records with in/out
public abstract class EdgeRecord : Record
{
    public RecordId? In { get; set; }
    public RecordId? Out { get; set; }
}

// User-defined edge with metadata
public class WorksIn : EdgeRecord
{
    public string Role { get; set; } = "";
    public DateTimeOffset Since { get; set; }
}
```

### SurrealQL Reference

```surql
-- Create edges with RELATE
RELATE person:alice->works_in->team:alpha CONTENT { role: "Lead", since: "2024-01-01" };

-- Single-hop traversal
SELECT ->works_in->team.name AS team_name FROM person:alice;
SELECT <-manages<-person.name AS manager FROM person:bob;

-- Multi-hop traversal
SELECT ->works_in->team->works_on->project.* FROM person:alice;

-- Wildcard edges (any type)
SELECT id, ->?->? FROM person;

-- Recursive / bounded depth
SELECT @.{2}->child_of->person AS grandparents FROM ONLY person:1;
SELECT @.{1..4}->has->(+) FROM planet:earth;
SELECT @.{..}->has->(+) FROM planet:earth;

-- Shortest path algorithm
SELECT @.{..2+shortest=person:star}->knows->person FROM person:you;

-- Path collection (all paths, all unique nodes)
SELECT @.{..+path}->knows->person FROM person:you;
SELECT @.{..+collect}->knows->person FROM person:you;

-- Edge metadata is first-class — edges have in, out, and custom properties
SELECT *, ->works_in AS membership, ->works_in->team.* FROM person FETCH membership;
```

### Planned C# API

```csharp
// Edge creation on IDocumentSession
await session.RelateAsync<WorksIn, Person, Team>(
    RecordId.Of<Person>("alice"),
    RecordId.Of<Team>("alpha"),
    new { Role = "Lead", Since = DateTimeOffset.UtcNow });

// Single-hop traversal
var team = await query.Graph<Person>()
    .Out<Team>("works_in")
    .FirstOrDefaultAsync();

// Multi-hop
var projects = await query.Graph<Person>()
    .Out<Team>("works_in")
    .Out<Project>("works_on")
    .ToListAsync();

// Recursive depth with intermediate nodes
var ancestors = await query.Graph<Person>()
    .In<Person>("child_of")
    .Depth(2, 5)
    .ToListAsync();

// Shortest path
var paths = await query.Graph<Person>()
    .Out<Person>("knows")
    .ShortestPath(RecordId.Of<Person>("charlie"))
    .ReturnPath()
    .ToPathListAsync();
```

### Schema Integration

```csharp
o.Schema.Edge<WorksIn, Person, Team>(edge =>
{
    edge.SchemaMode(SchemaMode.Strict);
    edge.Index(e => e.Role);
});
// → DEFINE TABLE works_in TYPE RELATION IN person OUT team SCHEMAFULL;
// → DEFINE FIELD role ON TABLE works_in TYPE string;
// → DEFINE INDEX idx_works_in_role ON TABLE works_in COLUMNS role;
```

## Multi-Database / Schema Support (Planned — Phase 17)

SurrealDB's `NAMESPACE → DATABASE` hierarchy maps directly to PostgreSQL's `DATABASE → SCHEMA` model. Each SurrealDB `DATABASE` is fully isolated — tables, fields, indexes, events, and functions in one are invisible to others.

| PostgreSQL | SurrealDB |
|------------|-----------|
| `CREATE DATABASE myorg` | `DEFINE NAMESPACE myorg` |
| `CREATE SCHEMA accounting` | `DEFINE DATABASE accounting` |
| `CREATE TABLE accounting.ledger` | `USE DB accounting; DEFINE TABLE ledger` |

Dali lets you map document types to different SurrealDB databases via `DocumentMapping<T>.Schema()`. The library uses `ForkSession()` to create isolated sub-sessions per database, avoiding the need for separate client pools.

### Critical Design Decisions (from Council Review)

| Decision | Rationale |
|----------|-----------|
| **ForkSession per DB, not inline `USE DB`** | SurrealDB's `USE` is an RPC method, not SurrealQL — it cannot be concatenated into a query string. `ForkSession()` creates a cloned session, then `.Use(ns, db)` switches context safely. |
| **`.Schema("sales")` not `.Database("sales")`** | Avoids naming collision with `StoreOptions.Database` (the default connection database). `Schema` aligns with PostgreSQL and Marten terminology. |
| **Cross-DB queries rejected at translation** | SurrealDB has no cross-database queries. A single LINQ expression spanning types from different databases throws `InvalidOperationException`. |
| **Multi-DB transactions rejected** | `SaveChangesAsync` throws if operations span multiple databases. Users needing cross-DB consistency must orchestrate compensating sagas manually. |
| **Schema auto-creation is opt-in** | `DEFINE DATABASE` is a high-privilege operation. `AutoCreateDatabases` defaults to `false`. |

### SurrealQL Reference

```surql
-- Create schemas (databases)
DEFINE DATABASE accounting;
DEFINE DATABASE hr;
DEFINE DATABASE sales;

-- Tables are isolated per database
USE DB accounting;
DEFINE TABLE ledger SCHEMAFULL;
DEFINE FIELD amount ON ledger TYPE float;

USE DB hr;
DEFINE TABLE employee SCHEMAFULL;
DEFINE FIELD name ON employee TYPE string;

USE DB sales;
DEFINE TABLE invoice SCHEMAFULL;
DEFINE FIELD total ON invoice TYPE float;
```

### Planned C# API

```csharp
// Config — per-type schema routing
var store = Documents.For(o =>
{
    o.Schema.For<Invoice>()
        .Schema("sales")                   // → USE DB sales; SELECT * FROM invoice
        .Index(i => i.Total);

    o.Schema.For<Employee>()
        .Schema("hr")                      // → USE DB hr; SELECT * FROM employee

    o.Schema.For<Product>()
        .Schema(null);                     // explicit reset → uses StoreOptions.Database

    // Schema auto-creation is opt-in (high privilege operation)
    // o.Schema.AutoCreateDatabases = true;
});
```

### Architecture

```
DocumentStore.InitializeAsync
  └→ SchemaManager
       └→ DEFINE DATABASE {name}          (only if AutoCreateDatabases = true)
       └→ DEFINE TABLE {table} {mode}
       └→ DEFINE FIELD ... ON TABLE ...

IQuerySession / IDocumentSession
  ├→ GetSessionForSchema("sales")         ForkSession() + Use(ns, "sales")
  ├→ GetSessionForSchema("hr")            ForkSession() + Use(ns, "hr")
  └→ GetSessionForSchema(null)            parent session (default database)

Query execution:
  ├→ SurrealQueryProvider resolves SchemaTarget(database, table)
  ├→ Routes to correct forked session
  └→ Executes on isolated session

SaveChangesAsync:
  ├→ Groups UnitOfWork operations by target database
  ├→ Rejects multi-DB groups with InvalidOperationException
  └→ Executes each group on its forked session
```

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
    Compiled/                              # Compiled queries
      ICompiledQuery.cs                    # Compiled query interface
      CompiledQueryProvider.cs             # Runtime compiled query execution
    Events/
      IEvents.cs                           # Event store interface
      EventStore.cs                        # Append, FetchStream, StartStream
      EventTriggerDefinition.cs            # SurrealDB native event trigger model
      EventTriggerManager.cs               # DEFINE EVENT / ALTER EVENT / REMOVE EVENT
    Functions/
      SurrealFunction.cs                   # User-defined function model
      FunctionManager.cs                   # DEFINE FUNCTION / REMOVE FUNCTION
    Graph/                                 # (Planned — Phase 16)
      EdgeRecord.cs                        # Base edge type with In/Out
      IGraphQuery.cs                       # Fluent graph query interface
      GraphQueryBuilder.cs                 # Step accumulator
      GraphQueryPlan.cs                    # Intermediate representation
      GraphSurrealQLGenerator.cs           # Plan → SurrealQL
      GraphResultDeserializer.cs           # CBOR response → results
      GraphQueryProvider.cs                # Wires session + deserialization
    Projections/
      IProjection.cs / IProjectionContext.cs
      InlineProjection.cs / SingleStreamProjection.cs / MultiStreamProjection.cs
      AsyncDaemon.cs                       # Background polling daemon
    Schema/
      SchemaManager.cs                     # DEFINE TABLE/FIELD/INDEX
      DocumentMapping.cs                   # Fluent index API
    Metadata/                              # Source-generated metadata
      MetadataRegistry.cs                  # ITypeMetadata, ITypeMetadata<T>, ConcurrentDictionary registry
      MetadataDispatch.cs                  # Registry-first dispatch with reflection fallback
    MultiTenancy/                          # Tenancy support (conjoined, per-db)
    Patching/                              # Partial update expressions
    SoftDelete/                            # Soft delete support
    Concurrency/                           # Optimistic concurrency (IVersioned, ConcurrencyException)

  Dali.SourceGenerators/                   # Roslyn source generator (netstandard2.0)
    DaliDocumentGenerator.cs               # IIncrementalGenerator: scans Record subclasses

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

All 14 implementation phases + metadata wiring audit fixes are complete. See [init-impl-plan.md](init-impl-plan.md) for the full plan, test counts, and backlog.

| Phase | Tests | Status |
|-------|-------|--------|
| 1. Core Document Store | 8 | ✅ |
| 2. Test Coverage | 30 | ✅ |
| 3. Projections | 37 | ✅ |
| 4. Multi-Tenancy + Schema | 49 | ✅ |
| 5. Query Translation | 64 | ✅ |
| 6. EF Core Bridge | 81 | ✅ |
| 7. Modular Config | 91 | ✅ |
| 8. Compiled Queries | 104 | ✅ |
| 9. Patch/Partial Updates | 110 | ✅ |
| 10. Optimistic Concurrency | 116 | ✅ |
| 11. Soft Delete | 124 | ✅ |
| 12. Database-per-Tenant | 134 | ✅ |
| 13. Server-Side Aggregates | 134 | ✅ |
| 14. Source Generators | 144 | ✅ |
| 15. Schema Modes, Events, RawQL & Functions | 144 | ✅ |
| 16. Graph API (RELATE, traversal, paths) | 144 | 📋 Planned |
| 17. Multi-Database / Schema Support | 144 | 📋 Planned |
| A+B+C+F. Metadata Wiring | 144 | ✅ |

## Source-Generated Metadata

Dali uses a Roslyn `IIncrementalGenerator` (`DaliDocumentGenerator`) to eliminate runtime reflection for document metadata. The generator scans all `Record` subclasses at compile time and emits per-type `{Type}Metadata.g.cs` classes that implement `ITypeMetadata<T>`.

### What Gets Generated

For each `Record` subclass (e.g. `Person`), the generator emits:

```csharp
internal sealed class PersonMetadata : ITypeMetadata<Person>
{
    public static readonly PersonMetadata Instance = new();
    static PersonMetadata() => MetadataRegistry.Register<Person>(Instance);

    // Properties — compile-time constants
    public string TableName => "person";
    public bool HasTenantId => true;
    public bool HasVersion => true;
    public string? VersionFieldName => "Version";

    // Typed accessors — no reflection
    public string? GetTenantId(Person entity) => entity.TenantId;
    public void SetTenantId(Person entity, string? tenantId) => entity.TenantId = tenantId;
    public long GetVersion(Person entity) => entity.Version;
    public void SetVersion(Person entity, long version) => entity.Version = version;
    public string? GetRecordId(Person entity) { /* extract from entity.Id RecordId */ }

    // Untyped delegates — bridge the object-typed hot paths
    public Func<object, long>? GetVersionAccessor => obj => ((Person)obj).Version;
    public Action<object, long>? SetVersionAccessor => (obj, v) => ((Person)obj).Version = v;
    public Func<object, string?>? GetRecordIdAccessor => obj => { /* cast + extract */ };
}
```

### Registration Flow

1. **Compile time:** Generator emits `{Type}Metadata` with static constructor calling `MetadataRegistry.Register<T>(instance)`
2. **Assembly load:** Static constructors run, populating the `ConcurrentDictionary` registry
3. **Runtime:** `MetadataDispatch` checks registry first — if present, uses generated code (zero reflection). Falls through to reflection for non-generated types.

### Wiring Status

| Runtime path | Before audit | After audit |
|-------------|--------------|-------------|
| Table name (persistence) | `MetadataDispatch` ✅ | ✅ |
| Table name (queries) | `Snake()` direct ✗ | `MetadataDispatch` ✅ |
| Tenant set (Store) | `GetProperty("TenantId")` ✗ | `ITypeMetadata<T>.SetTenantId` ✅ |
| Tenant get (Delete/Load) | `GetProperty("TenantId")` ✗ | `ITypeMetadata<T>.GetTenantId` ✅ |
| Version read/write | `GetProperty(name).GetValue/SetValue` ✗ | `GetVersionAccessor`/`SetVersionAccessor` ✅ |
| RecordId extraction | `GetProperty("Id")` ✗ | `GetRecordIdAccessor` ✅ |
| Non-generated types | Reflection fallback | Reflection fallback (unchanged) |

## Low-Level SDK Access (`Advanced` API)

Dali exposes the underlying `SurrealDb.Net` client and session objects for advanced scenarios the library does not yet abstract — raw queries, multi-database access, administrative operations, live query subscriptions, schema migrations, etc.

### Lifetime Compatibility

Per the SurrealDb.Net SDK documentation:

| Class | Singleton | Scoped | Transient |
|---|---|---|---|
| `SurrealDbClient` | ✅ | ❌ | ❌ |
| `SurrealDbSession` | ❌ | ✅ | ✅ |

`SurrealDbClient` is a thread-safe connection pool manager (singleton). `SurrealDbSession` is a lightweight, scoped-per-request session that auto-opens/closes connections.

### API Shape

```csharp
// Flat convenience (existing) — kept for backward compatibility
store.Client                        // ISurrealDbClient

// Structured escape hatch (recommended for new code)
store.Advanced.SurrealDbClient      // ISurrealDbClient — the underlying singleton
store.Advanced.CreateSessionAsync() // Task<ISurrealDbSession> — factory, not a property
store.Advanced.CreateSessionAsync("ns", "db") // pre-configured session
```

### Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Flat `store.Client` | **Kept**, soft `[Obsolete]` pointing to `Advanced` | Backward compat; guides users to canonical path |
| Namespace | **`.Advanced`** — matches Marten convention | Dali targets Marten users; learn-once pattern |
| Session access pattern | **Factory method**, not property | `ISurrealDbSession` is scoped — a property implies cheap get. Factory makes allocation + disposal explicit. |
| Async primacy | **`CreateSessionAsync()`** as canonical | `ISurrealDbClient.CreateSession()` returns `Task<ISurrealDbSession>` internally; sync wrapper risks deadlock |
| DI auto-registration | **Opt-in only** (`registerRawTypes: true`) | Avoids dual-registration conflicts with `AddSurreal()` |

### Session Ownership

Sessions obtained from `Advanced.CreateSessionAsync()` are **not managed by Dali**. The caller must dispose them:

```csharp
await using var session = await store.Advanced.CreateSessionAsync(ct);
var results = await session.RawQuery("SELECT * FROM person WHERE age > $age",
    new Dictionary<string, object> { ["age"] = 30 }, ct);
```

### DI Integration

- **Default:** `services.AddDali(...)` registers Dali types only. Raw `ISurrealDbClient`/`ISurrealDbSession` are not in DI.
- **Opt-in:** `services.AddDali(options => { ... }, registerRawTypes: true)` also registers raw SDK types.
- **Manual:** Users can call `services.AddSurreal()` separately for full control.

### Implementation

```csharp
public interface IDaliAdvanced
{
    ISurrealDbClient SurrealDbClient { get; }
    Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default);
    Task<ISurrealDbSession> CreateSessionAsync(string ns, string db, CancellationToken ct = default);
}

public interface IDocumentStore : IAsyncDisposable
{
    ISurrealDbClient Client { get; }           // kept for backward compat
    IDaliAdvanced Advanced { get; }            // new
    // ...
}
```

### Risks

| Risk | Mitigation |
|---|---|
| Session disposal leaks | XML docs + `<b>` emphasis on caller responsibility |
| `store.Client` deprecation fatigue | `[Obsolete]` without `error: true`; upgrade in 2.0 |
| Tenant confusion (DatabasePerTenant) | `Advanced.SurrealDbClient` returns root client; document this |
| DI dual-registration | `TryAdd*` semantics in opt-in overload |

## ⚠️ Known Limitations

### 1. CBOR Deserialization: Non-`Record` Types

**Problem:** SurrealDB's embedded engine (`SurrealDbEmbeddedEngine`) uses [Dahomey.Cbor](https://github.com/dahomey-technologies/Dahomey.Cbor) for response deserialization. `SurrealDbResponse.GetValue<T>(0)` invokes Dahomey.Cbor directly — if `T` is a plain POCO that doesn't extend `SurrealDb.Net.Models.Record`, deserialization **fails** with a CborException.

The remote (WebSocket/HTTP) engine does not share this problem — it uses JSON internally.

**Impact:** Any test or embedded scenario using plain POCOs breaks on deserialization. This affects `LoadAsync<T>`, raw queries, aggregate results (e.g. `math::sum`), and any generic mapper.

**Workaround:** Use `GetValue<List<object>>(0)` to receive raw CBOR boxes, then JSON-serialize and deserialize:

```csharp
var raw = response.GetValue<List<object>>(0);
var json = JsonSerializer.Serialize(raw);
var result = JsonSerializer.Deserialize<T>(json);
```

For aggregate queries, define a `Record`-derived DTO with `[CborProperty("math::sum")]` attributes:

```csharp
internal sealed class SumResult : SurrealDb.Net.Models.Record
{
    [CborProperty("math::sum")]
    public decimal Value { get; set; }
}
```

This is used internally by `QuerySession.CountAsync` and aggregate extension methods.

**Rule of thumb:** If a type appears in a `Where<T>()` result or a `LoadAsync<T>` call, extend `Record`. If you cannot (third-party types), use the JSON round-trip workaround.

### 2. Live Queries: WebSocket Only

`LiveTable<T>()` / `LiveRawQuery<T>()` / `WatchTableAsync<T>()` only work with the **WebSocket engine** (`ws://` or `wss://` connection strings). The HTTP engine and embedded engine throw `NotSupportedException`.

### 3. DB-per-Tenant: Advanced Client Disclaimer

`StoreOptions.Advanced.SurrealDbClient` returns the **root** client — not the per-tenant client. Per-tenant isolation only applies to sessions created through the store. Direct use of `Advanced.SurrealDbClient` bypasses tenant routing.

## Wolverine Integration (`WolverineFx.Dali`)

Dali provides a Wolverine persistence/transport package (`WolverineFx.Dali`) analogous to `WolverineFx.Marten`. It delivers full parity: saga storage, transactional middleware, outbox, projection distribution, and message persistence + transport (inbox/outbox, durable queues).

This is a **separate NuGet package** (`WolverineFx.Dali`) within the Dali repo (`src/WolverineFx.Dali/`), referencing both `WolverineFx` and `Dali`.

> **Note:** The Saga pattern itself is built into Wolverine core — it has a `Saga` base class with state lifecycle, handler dispatch, and completion semantics. Store packages only provide the persistence backend. `WolverineFx.Dali` implements `ISagaStorage` — it does not reinvent the saga framework.

### Architecture

```
WolverineFx.Dali
├── DaliMessageStore : IMessageStore / IMessageInbox / IMessageOutbox / IDeadLetters
│   └── Direct ISurrealDbClient.RawQuery() (standalone, not MessageDatabase<T>)
│       Tables: wolverine_incoming_envelopes, wolverine_outgoing_envelopes,
│               wolverine_dead_letters, wolverine_nodes
│       Claim pattern: UPDATE ... RETURN BEFORE (atomic, single round-trip)
│
├── DaliTransport : ITransport (scheme: dali://)
│   ├── DaliQueueListener — poll loop + atomic claim
│   └── DaliQueueSender — INSERT into outgoing table
│
├── Codegen Frames (mirrors WolverineFx.Marten exactly)
│   ├── OpenDaliSessionFrame — opens IDocumentSession
│   ├── FlushOutgoingMessagesOnDaliCommit : IDocumentSessionListener
│   │   ├── BeforeSaveChangesAsync → marks handled
│   │   └── AfterCommitAsync → FlushOutgoingMessagesAsync()
│   ├── DaliSessionSaveChangesFrame — session.SaveChangesAsync()
│   └── FlushDaliOutgoingMessagesFrame — context.FlushOutgoingMessagesAsync()
│
├── DaliSagaStorage : ISagaStorage
│   └── Saga persistence via IDocumentSession (Load/Store/Update/Delete)
│
├── OutboxedDaliSessionFactory
│   └── Creates IDocumentSession pre-enrolled in Wolverine's outbox
│
└── DaliProjectionCoordinator
    └── Wolverine-managed projection distribution (future)

         All operations ↓
    SurrealDB (same namespace/database)
    ├── Domain tables (person, order, event, etc.)
    └── Wolverine envelope tables (wolverine_*)
```

### Envelope Storage Schema

SurrealDB tables for message persistence:

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

SurrealDB's closest analog to PostgreSQL `SELECT ... FOR UPDATE SKIP LOCKED`:

```surql
UPDATE wolverine_incoming_envelopes
SET owner_id = $nodeId, status = 'Incoming'
WHERE status = 'Scheduled'
  AND execution_time <= time::now()
ORDER BY execution_time ASC LIMIT $batchSize
RETURN BEFORE
```

Single atomic statement — one round-trip, zero TOCTOU races.

### Transport Wake-up

| Mode | Mechanism | Default |
|------|-----------|---------|
| **Polling** | Configurable interval (default 5s) | ✅ Default |
| **Live Query** | SurrealDB WebSocket `LIVE SELECT` push | ❌ Opt-in |

Hybrid pattern mirrors Wolverine's PostgreSQL `LISTEN`/`NOTIFY` + polling fallback.

### Transactional Middleware

Mirrors `WolverineFx.Marten` exactly via codegen frames:

| Order | Frame | Action |
|-------|-------|--------|
| 1 | `OpenDaliSessionFrame` | `var session = factory.OpenSession(ctx)` |
| 2 | *(handler runs)* | Uses `IDocumentSession` |
| 3 | `DaliSessionSaveChangesFrame` | `await session.SaveChangesAsync(ct)` |
| 4 | `FlushDaliOutgoingMessagesFrame` | `await ctx.FlushOutgoingMessagesAsync()` |

`FlushOutgoingMessagesOnDaliCommit` (`IDocumentSessionListener`) ensures correct ordering:
1. `BeforeSaveChangesAsync` — marks envelopes as handled
2. `SaveChangesAsync` — atomically persists domain data + envelope status
3. `AfterCommitAsync` — dispatches outgoing messages

### Registration

```csharp
builder.Services.AddDali(o => { /* ... */ });

// Connect Wolverine to Dali
builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithDali();
    opts.Policies.AutoApplyTransactions();
});
```

Or the Marten-style chained syntax:

```csharp
builder.Services.AddDali(o => { /* ... */ })
    .IntegrateWithWolverine();
```

### Key Risks

| Risk | Mitigation |
|------|------------|
| No multi-document atomicity | Accept eventual consistency; domain data persists first via listener pattern. Long-term: batch into `BEGIN TRAN`/`COMMIT TRAN`. |
| `RETURN BEFORE` behavior on older SurrealDB | Test against SurrealDB ≥ 2.x. Fallback to two-step claim (SELECT + versioned UPDATE). |
| RawQuery latency vs Npgsql direct | Batch envelope ops into multi-statement scripts. Single-statement claim+return minimizes round-trips. |
| `MessageDatabase<T>` base class incompatible | Build `DaliMessageStore` as standalone class — SurrealDB has no `DbConnection`/`DbCommand` abstraction. |

### Implementation Phases

| Phase | Scope | Key Classes |
|-------|-------|-------------|
| 1. Core Message Store | Schema init, envelope CRUD, durability agent timers | `DaliMessageStore`, `DaliEnvelope` |
| 2. Transport | Queue listener/sender, poll loop, options | `DaliTransport`, `DaliQueueListener`, `DaliQueueSender` |
| 3. Middleware + Sagas | Session factory, codegen frames, saga storage | `OutboxedDaliSessionFactory`, `DaliSagaStorage`, 6 codegen frames |
| 4. Projections | Projection distribution, multi-tenancy, compliance tests | `DaliProjectionCoordinator` |

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

[^2]: Dave MacLeod, "Visualising your data with Surrealist's Graph view," SurrealDB Blog, Mar 2025. Demonstrates graph relationships with RELATE, multi-hop traversals, recursive shortest-path queries, and the interactive Graph view in Surrealist. [`Source`](https://surrealdb.com/blog/visualising-your-data-with-surrealists-graph-view)
