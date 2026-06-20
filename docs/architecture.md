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
