# AeroDB — SurrealDB Document Store

A .NET document store for SurrealDB with a Marten-compatible fluent API — idiomatic .NET sessions, schema management, indexing, and LINQ querying over SurrealDB's multi-model engine.

## Goal

**Be the MartenDB for SurrealDB.** Bring the ergonomic document-session pattern, fluent schema configuration, and LINQ query pipeline that the .NET ecosystem loves to SurrealDB's document, graph, and search capabilities. If you've used Marten with PostgreSQL, AeroDB should feel like home.

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
    .MtreeIndex(x => x.Embedding, 768)                        // vector (MTREE)
    .DiskannIndex(x => x.Embedding, 1536, vectorType: "F16")  // vector (DiskANN)
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

## Marten Parity

| Feature | Marten (PostgreSQL) | AeroDB (SurrealDB) |
|---------|---------------------|-------------------|
| Document sessions | `IDocumentSession` | `IDocumentSession` |
| Fluent schema | `Schema.For<T>().Index()` | `Schema.For<T>().Index()` |
| Anonymous-type multi-column index | `Index(x => new { ... })` | `Index(x => new { ... })` |
| LINQ querying | `session.Query<T>()` | `session.Query<T>()` |
| Full-text search | PostgreSQL tsvector | SurrealDB FTS |
| Vector search | pgvector extension | HNSW / MTREE / DiskANN |
| Multi-tenancy | Conjoined / separate | Native tenant filtering |
| Event sourcing | Marten Events | AeroDB Events (WolverineFx integration) |

---

## Credits

AeroDB was solely and greatly inspired by **Jeremy D. Miller** and the genius developers over at **JasperFx** and their brilliant library **MartenDB** for PostgreSQL, which we have used for years. They just released **Polecat** for MSSQL, which is awesome. We recently had a need to use SurrealDB in a project and decided to follow the MartenDB patterns. AeroDB depends on **SurrealDB.net**, another awesome OSS library from the SurrealDB team.

- [MartenDB](https://martendb.io/)
- [Polecat](https://polecat.jasperfx.net/)
- [SurrealDB.net](https://github.com/surrealdb/surrealdb.net)
