---
title: FAQ
description: Frequently asked questions about AeroDB
---

### What is AeroDB?

AeroDB is a .NET document store and ORM for [SurrealDB](https://surrealdb.com), providing a Marten-compatible fluent API. It enables .NET developers to work with SurrealDB using familiar patterns — `IDocumentSession`, `IQuerySession`, LINQ queries, event sourcing, and unit-of-work — while leveraging SurrealDB's native multi-model capabilities.

### How is AeroDB different from MartenDB?

MartenDB layers a document-store abstraction over PostgreSQL, which is fundamentally a relational database. AeroDB runs on SurrealDB's native multi-model engine, which natively supports documents, graphs, time-series, vectors, and key-value — all in a single database with a unified query language (SurrealQL). This means you get graph traversals, vector search, and live queries without bolting on additional infrastructure.

### What version of SurrealDB is required?

AeroDB targets **SurrealDB 3.1 or later** and is not compatible with SurrealDB 2.x. The 3.x line introduced breaking changes to the query engine and wire protocol that AeroDB depends on.

### Is AeroDB production-ready?

AeroDB is currently in **alpha** (v0.0.8.x). APIs are still evolving and may break between releases. It is not recommended for production workloads. We recommend evaluation and prototyping only until a stable v1.0 release.

### Can I use LINQ with AeroDB?

Yes. AeroDB includes a full LINQ provider that translates LINQ expressions into SurrealQL. You can write strongly-typed queries like `session.Query<User>().Where(u => u.Email == "test@example.com")` and have them executed server-side. The provider supports `Where`, `Select`, `OrderBy`, `Count`, `Any`, `FirstOrDefault`, `Take`/`Skip`, and many more operators.

### Does AeroDB support multi-tenancy?

Yes. AeroDB has built-in tenant isolation. You configure a tenant ID per session, and all queries automatically inject a tenant filter into the SurrealQL. This works at the database level using SurrealDB's row-level scoping, ensuring tenants cannot see each other's data even if a query omits the tenant clause.

### How do I perform graph queries (traversals)?

Use the `session.Graph<T>()` API. For example, `session.Graph<User>().Out("friend").OfType<User>().ToListAsync()` traverses outgoing `friend` edges and returns connected `User` documents. Traversal methods include `Out()`, `In()`, `OutE()`, `InE()`, `Both()`, with support for edge filtering, depth control, and recursive patterns.

### Does AeroDB support vector search?

Yes. AeroDB works with SurrealDB's vector indexes — both **HNSW** (hierarchical navigable small world) and **DiskANN** — for semantic and similarity search. You can define vector fields on your documents, create vector indexes via the schema API, and query using `session.Query<T>().NearestNearest(x => x.Embedding, targetVector, limit: 10)`.

### How does event sourcing work in AeroDB?

AeroDB implements a Marten-style event store on top of SurrealDB:

- **Append** events to a stream using `session.Events.Append(Guid.NewGuid(), new UserRegistered { ... })`
- **Project** events to read models using inline or async projections
- **Aggregate** streams on-the-fly with `session.Events.AggregateStream<T>(streamId)`
- **Subscribe** to the change log via `session.ChangeFeed()` for real-time event consumption

Streams can be single-tenant or multi-tenant, and events are stored as SurrealDB records for queryability.

### Can I execute raw SurrealQL?

Yes. Use `session.RawQueryAsync<T>("SELECT * FROM user WHERE email = $email", new { email = "a@b.com" })` for queries, or `session.ExecuteSqlAsync("CREATE user SET name = $name", params)` for commands. This gives you full access to SurrealQL features like graph pattern matching, live queries, and DEFINE statements without leaving the session context.

### What about Entity Framework Core integration?

AeroDB offers an **optional** `AeroDB.EntityFrameworkCore` NuGet package that bridges AeroDB sessions with EF Core's `DbContext`. This is useful for hybrid workloads — for example, using EF Core for migrations and relational queries alongside AeroDB's document/graph APIs. The bridge is in active development and is not yet GA.

### How do I get started?

1. Install the NuGet package: `dotnet add package AeroDB`
2. Configure your SurrealDB connection: `builder.Services.AddAeroDB("namespace", "database", opts => opts.Connect("http://localhost:8000"));`
3. Initialize the store: `await store.InitializeAsync()`
4. Open a session and start working: `var session = store.LightweightSession();`

See the [Getting Started](/docs/getting-started) guide for a full walkthrough.

### Does AeroDB support change tracking and unit-of-work?

Yes. `IDocumentSession` implements the unit-of-work pattern. You call `session.Store(document)`, make changes, and then `session.SaveChangesAsync()` flushes all inserts, updates, and deletes in a single batched SurrealQL transaction. The change-tracking mode is configurable per-session.

### Can I use AeroDB with ASP.NET Core and Orleans?

Yes. AeroDB integrates with ASP.NET Core via `AddAeroDB` in `IServiceCollection` (managing connection lifecycle, scoped sessions per request). For **Orleans** silos, AeroDB can be used as a custom storage provider, allowing grains to persist to SurrealDB while still using Orleans' in-memory activation model.

### How does AeroDB handle schema generation?

AeroDB can auto-generate SurrealDB schemas (DEFINE TABLE, DEFINE FIELD) from your .NET document classes using the `store.Storage.ApplyAllConfiguredChangesToDatabaseAsync()` API. You can also define indexes, vector indexes, and graph edges declaratively via attributes or fluent configuration on `StoreOptions`.

### Where can I ask questions or report issues?

Open a [GitHub Issue](https://github.com/microbian-systems/AeroDB/issues) for bugs or feature requests, start a [Discussion](https://github.com/microbian-systems/AeroDB/discussions) for questions, or check the [docs](/docs) for guides and API reference.
