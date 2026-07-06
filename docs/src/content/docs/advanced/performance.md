---
title: Performance
description: Optimize AeroDB for production workloads
---

## Session Lifecycle

Open sessions late, dispose promptly. Use `LightweightSession` (tracking mode `DocumentTracking.None`) unless you need identity-map change tracking:

```csharp
await using var session = store.LightweightSession();
session.Store(doc);
await session.SaveChangesAsync();
```

## Batching Operations

`SaveChangesAsync` flushes all pending operations in one transaction. Batch eagerly:

```csharp
session.Store(users);       // 1000 items, 1 trip
session.Delete(oldDocs);
await session.SaveChangesAsync(); // atomic
```

Batch reads with `IBatchedQuery`:

```csharp
var batch = session.CreateBatchQuery();
var usersTask = batch.Query<User>().Where(u => u.IsActive).ToListAsync();
var countTask = batch.Query<User>().CountAsync();
await batch.ExecuteAsync(); // single round-trip
```

## Compiled Queries

Pre-parse hot-path queries with `CompiledQuery`:

```csharp
private static readonly CompiledQuery<User, User?> GetByEmail =
    CompiledQuery.Create((IQueryable<User> q, string email) =>
        q.FirstOrDefaultAsync(u => u.Email == email));

var user = await GetByEmail(session.Query<User>(), "alice@example.com");
```

For maximum throughput, implement `ICompiledQuery<TDoc, TOut>` to cache the query plan.

## Index Strategies

Define indexes for hot query paths during configuration:

```csharp
opts.Schema.For<User>()
    .Index(u => u.Email, c => c.IsUnique())
    .Index(u => new { u.LastName, u.FirstName })
    .FullTextIndex(u => u.Bio, "english");
```

Composite indexes support leftmost-prefix queries.

## Connection Pooling

Register the store as a singleton — it manages pooling internally:

```csharp
builder.Services.AddSingleton<IDocumentStore>(sp =>
    Documents.For(opts => opts.Connection("http://localhost:8000", "ns", "db")));
```

## Async Patterns

Use `ConfigureAwait(false)` in library code. Never mix blocking calls (`.Result`, `.Wait()`) with async — this causes thread-pool starvation.

## Profiling SurrealQL

Inspect generated queries:

```csharp
opts.Logger = (sql, parameters) =>
    Console.WriteLine($"[SurrealQL] {sql} | params: {parameters}");

var (sql, parameters) = session.Query<User>()
    .Where(u => u.Age > 18)
    .ToDebugString();
```

## Bulk Operations

For large imports, bypass per-document overhead:

```csharp
await store.BulkInsertAsync(users); // single multi-value INSERT
```

Bulk operations skip identity-map tracking and are significantly faster for initial loads.
