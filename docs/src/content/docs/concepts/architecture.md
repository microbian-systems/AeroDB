---
title: Architecture
description: How AeroDB is built
---

AeroDB is a **document store** and **event sourcing** library for .NET, built on top of [SurrealDB](https://surrealdb.com). It translates the Marten library's programming model — unit-of-work sessions, LINQ queries, event streams, projections — into SurrealQL operations against SurrealDB's document-relational database.

## Store and Sessions

The `IDocumentStore` (implemented by `DocumentStore`) is a **singleton** that manages a connection to SurrealDB. It owns schema initialization, projection lifecycle, and acts as a session factory.

Sessions are **short-lived** and lightweight. There are three kinds:

- **`LightweightSessionAsync()`** — No identity map or change tracking (`DocumentTracking.None`). Best for most read/write workloads.
- **`OpenSessionAsync(SessionOptions)`** — Configurable tracking. Use `DocumentTracking.IdentityOnly` for an identity map, or `DocumentTracking.DirtyTracking` to detect changes automatically.
- **`QuerySessionAsync()`** — Read-only session. No `Store`/`Delete` operations.

```csharp
await using var store = Documents.For(opts =>
{
    opts.Connection("http://localhost:8000", "myns", "mydb");
});

// Lightweight (no tracking)
await using var session = await store.LightweightSessionAsync();
session.Store(new User { Name = "Alice" });
await session.SaveChangesAsync();
```

## Unit of Work

`DocumentSession` implements the **unit of work** pattern. Calls to `Store<T>()` and `Delete<T>()` queue changes in memory. When `SaveChangesAsync()` is called, AeroDB wraps all operations in a SurrealDB transaction:

1. Optimistic concurrency checks (if enabled)
2. Increment version fields
3. Persist added/modified/deleted entities
4. Append events (if event sourcing is enabled)
5. Run inline projections
6. Commit the transaction

## LINQ to SurrealQL

AeroDB translates .NET LINQ expressions into SurrealQL queries through a custom `ISurrealDbQueryable<T>` provider. The translation walks expression trees and maps to SurrealQL syntax:

```csharp
// LINQ expression → SurrealQL
var admins = await session.Query<User>()
    .Where(u => u.Role == "admin" && u.CreatedAt > cutoff)
    .OrderBy(u => u.Name)
    .ToListAsync();
// → SELECT * FROM user WHERE role = 'admin' AND created_at > d'2024-01-01T00:00:00Z' ORDER BY name;
```

Raw SurrealQL is also available via `RawQueryAsync<T>()` and `ExecuteSqlAsync()`.

## Namespace → Database → Table Hierarchy

AeroDB mirrors SurrealDB's three-level namespace:

| SurrealDB | AeroDB Config | Marten Equivalent |
|-----------|---------------|-------------------|
| Namespace | `Options.Namespace` | (logical cluster) |
| Database  | `Options.Database`  | PostgreSQL schema  |
| Table     | Entity type name (snake_case) | PostgreSQL table |

Unlike Marten (which uses PostgreSQL schemas and tables), AeroDB uses SurrealDB's `NS / DB / table` hierarchy. Each entity type maps to a SurrealDB table. Table names are derived from the class name in snake_case (`User` → `user`, `OrderItem` → `order_item`).

## Source Generators

The `AeroDB.SourceGenerators` package emits compile-time metadata for entity types, eliminating reflection at runtime:

- **Document shims** — CBOR-compatible record wrappers for `IEntity<TId>` types
- **Configurator dispatch** — auto-applies `IConfigureAeroDB` implementations from DI
- **Event projection dispatch** — fast method routing instead of reflection-based `Apply`/`Create` dispatch
