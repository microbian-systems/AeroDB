![AeroDB](assets/aero-logo-sml.png)

# AeroDB — SurrealDB Document Store for .NET

> **⚠️⚠️⚠️ ALPHA** — This library is in active development. APIs may change without notice. Not recommended for production use. ⚠️⚠️⚠️
>
> **Compatible with SurrealDB 3.x and higher only.** Not compatible with SurrealDB 2.x.

A .NET document store for SurrealDB with a Marten-compatible fluent API — idiomatic .NET sessions, schema management, indexing, and LINQ querying over SurrealDB's multi-model engine.

## Goal

AeroDB brings a proven .NET document-store pattern — fluent sessions, LINQ querying, schema-first indexing — to **SurrealDB's true multi-model engine**. While MartenDB and Polecat layer document-store access on top of PostgreSQL and MSSQL (relational databases at their core), AeroDB unlocks SurrealDB's native document, graph, and time-series models in a single database. Graph traversals over relations, temporal aggregations, full-text and vector search, live queries, and geospatial operations all happen natively — no translation layers, no polyglot persistence. For AI workloads, SurrealDB's vector indexing (HNSW, DiskANN) and `ml::` inference functions make AeroDB a natural fit for semantic search, RAG pipelines, and embedding-powered .NET applications.

If you've used Marten with PostgreSQL, AeroDB should feel like home — but the database underneath is built for a multi-model world.

---

## Quick Start

```csharp
// Configure
var store = Documents.For(opts =>
{
    opts.Namespace = "myapp";
    opts.Database   = "production";

    // Schema & indexes
    opts.Schema.For<User>()
        .Identity(u => u.Id)
        .Index(u => u.Email, c => c.IsUnique())
        .Index(u => new { u.FirstName, u.LastName })
        .FullTextIndex(u => u.Bio, "english");
});

await store.InitializeAsync();

// Write
await using var session = store.LightweightSession();
session.Store(new User { Name = "Alice", Email = "alice@example.com" });
await session.SaveChangesAsync();

// Read
await using var query = store.QuerySession();
var alice = await query.Query<User>()
    .FirstOrDefaultAsync(u => u.Email == "alice@example.com");
```

---

## API Surface

### Document Sessions

| Method | Purpose |
|--------|---------|
| `store.LightweightSession()` | Read/write, no change tracking |
| `store.QuerySession()` | Read-only |
| `session.Store(entity)` | Insert or update |
| `session.Delete(entity)` | Remove |
| `session.Query<T>()` | LINQ query entry point |
| `session.SaveChangesAsync()` | Commit unit of work |

### Schema & Indexing

```csharp
opts.Schema.For<T>()
    .Identity(x => x.Id)                                    // designate primary key
    .Index(x => x.Prop)                                      // single-column btree
    .Index(x => x.Prop, c => { c.IsUnique(); c.WithName("…"); })  // with options
    .Index(x => new { x.FirstName, x.LastName })              // multi-column composite via anonymous type
    .UniqueIndex(x => x.Prop)                                 // shortcut unique
    .FullTextIndex(x => x.Body, "english")                    // full-text search
    .HnswIndex(x => x.Embedding, 1536)                        // vector (HNSW)
    .DiskannIndex(x => x.Embedding, 1536, vectorType: "F16")  // vector (DiskANN — SurrealDB 3.1+ server)
    .SpatialIndex(x => x.Location)                             // geo-spatial marker
    .MultiTenanted()                                           // tenant-id filtered
    .SetSchemaMode(SchemaMode.Strict)                          // SCHEMAFULL / Flexible
    .Schema("analytics_db");                                   // route to a SurrealDB database
```

> **Schema vs Database**: AeroDB's `.Schema("name")` is named for Marten API parity, where it maps to a PostgreSQL schema. In SurrealDB, the hierarchy is **Namespace → Database → Table** (no RDBMS schemas). Under the hood, `.Schema("name")` switches to a different SurrealDB **database** within the same namespace via `USE NS x DB name`. This lets you logically partition tables (e.g., separate `sales` and `analytics` databases in a single store).

### LINQ Querying

```csharp
var results = await session.Query<User>()
    .Where(u => u.Age >= 18 && u.Name.Contains("A"))
    .OrderBy(u => u.Name)
    .Skip(20).Take(10)
    .ToListAsync();
```

---

## Features

### Event Sourcing & Change Logs

Full event sourcing with append, read, and projection support. Stream management via `IAggregate` and `IEvent<T>` with inline or async projections. A change-log feed captures ordered domain events for audit trails and CQRS pipelines.

```csharp
var stream = await events.StartStream<Order>(orderId, new OrderPlaced { ... });
await session.SaveChangesAsync();

var aggregate = await events.AggregateStreamAsync<Order>(orderId);
```

### Live Queries

Subscribe to real-time changes on any SurrealDB table. AeroDB wraps SurrealDB's `LIVE SELECT` capabilities into a push-based `IObservable<T>` interface — ideal for reactive UIs, dashboards, and cache invalidation.

```csharp
var live = await session.LiveQuery<User>()
    .Where(u => u.Status == "active")
    .SubscribeAsync(change => Console.WriteLine($"Change: {change.Action} — {change.Data?.Id}"));

// Dispose to unsubscribe
await live.DisposeAsync();
```

### Reactive Streams

Bridge live queries into Rx.NET `IObservable<T>` streams for declarative event processing. Filter by action type (created/updated/deleted), accumulate state over time, or compose with standard Rx operators — all without polling.

```csharp
// Shortcut: filter by action directly on the builder
var created = session.LiveQuery<User>()
    .CreatedRecords()
    .Subscribe(user => Console.WriteLine($"New user: {user.Name}"));

var updated = session.LiveQuery<User>()
    .UpdatedRecords()
    .Subscribe(user => Console.WriteLine($"Updated: {user.Name}"));

var deleted = session.LiveQuery<User>()
    .DeletedRecords()
    .Subscribe(id => Console.WriteLine($"Deleted: {id}"));

// Or pipe through Rx.NET for custom composition
var creations = session.LiveQuery<User>()
    .ToObservable()
    .SelectCreatedRecords()
    .Subscribe(user => Console.WriteLine($"New user: {user.Name}"));

// Accumulate state from a live stream
var state = await session.LiveQuery<Order>()
    .ToObservable()
    .AggregateRecords(new Dictionary<string, Order>());
```

---

### Graph Traversal

Native graph queries leveraging SurrealDB's relation support. Traverse edges, follow paths, and project graph results with a fluent builder — no manual SurrealQL required.

```csharp
var friends = await session.Graph<User>()
    .Out<User>(edge => edge.FriendsWith)
    .Where(f => f.Age > 21)
    .ToListAsync();
```

### Time-Series

Bucketed time-series aggregation with temporal grouping, windowing, and downsampling. Built on SurrealDB's native temporal functions for efficient analytics over time-bounded data.

```csharp
var hourly = await session.TimeSeries<SensorReading>()
    .BucketBy(TimeSpan.FromHours(1))
    .Average(r => r.Temperature)
    .Between(start, end)
    .ToListAsync();
```

### Spatial Queries

Geo-spatial filtering and ordering via SurrealDB's geometry type. Supports within-radius, bounding-box, and distance-order queries.

```csharp
var nearby = await session.Query<Venue>()
    .Where(v => v.Location.IsWithinRadius(myPosition, 5000))
    .ToListAsync();
```

### Compiled Queries

Pre-compiled, parameterized query plans for hot paths. Avoid re-translating LINQ expressions on every invocation — ideal for high-throughput endpoints and batch processing.

```csharp
public class UsersByCity : CompiledQuery<User>
{
    public string City { get; set; }
    public override Expression<Func<IQueryable<User>, IQueryable<User>>> Query()
        => q => q.Where(u => u.City == City);
}

var nycUsers = await session.QueryAsync(new UsersByCity { City = "NYC" });
```

### Raw Queries

Direct SurrealQL execution when LINQ isn't enough. Available on all session types for ad-hoc queries, administrative commands, or SurrealDB-specific features not yet abstracted.

```csharp
var results = await session.RawQueryAsync<User>("SELECT * FROM user WHERE age > $min", new { min = 21 });
await session.ExecuteSqlAsync("DEFINE INDEX email_idx ON TABLE user COLUMNS email UNIQUE");
```

### Multi-Tenancy

Built-in tenant isolation with automatic filter injection. Configure per-entity or globally, and all queries, inserts, and deletes are scoped to the active tenant ID.

### Batching & Bulk Operations

Group multiple operations into a single SurrealQL transaction for reduced round-trips. Batch inserts, deletes, and mixed operations with atomic commit.

### WolverineFx Integration

First-class integration with [Wolverine](https://wolverinefx.net/) for durable message queues, outbox patterns, saga persistence, and scheduled message delivery — all backed by SurrealDB.

### Source Generators

Roslyn incremental generators for compile-time metadata (table names, record IDs, version fields) — no runtime reflection, no magic strings.

### EF Core Bridge

Optional `AeroDB.EntityFrameworkCore` package coordinates transactions between AeroDB sessions and EF Core `DbContext` under a shared SurrealDB transaction. Use for hybrid workloads where part of your data flows through the document API and part through EF Core.

### SurrealML Integration

Experimental `AeroDB.ML` package exposes SurrealDB's `ml::` inference functions through a typed LINQ-like API — run model predictions directly inside your SurrealQL queries.

---

## Credits

AeroDB was solely and greatly inspired by **Jeremy D. Miller** and the genius developers over at **JasperFx** and their brilliant library **MartenDB** for PostgreSQL, which we have used for years. AeroDB depends on **SurrealDB.net**, another awesome OSS library from the SurrealDB team.

- [MartenDB](https://martendb.io/)
- [Polecat](https://polecat.jasperfx.net/)
- [SurrealDB.net](https://github.com/surrealdb/surrealdb.net)
