---
title: Create a Database
description: Connect to SurrealDB and store your first document
---

## Goal

Open a connection to SurrealDB, create a document, and verify it's stored.

## Code

```csharp
using AeroDB;

// 1. Configure the document store
var store = DocumentStore.For(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Database("myapp");
    cfg.Namespace("production");
    cfg.AutoCreateSchemaObjects = AutoCreate.All;
});

// 2. Open a session
await using var session = store.LightweightSession();

// 3. Insert a document
session.Store(new User
{
    Name = "Alice",
    Email = "alice@example.com",
    Active = true
});
await session.SaveChangesAsync();

// 4. Verify
var alice = await session.Query<User>()
    .FirstOrDefaultAsync(u => u.Name == "Alice");
Console.WriteLine($"Created user: {alice!.Id}");
```

## Explanation

- `DocumentStore.For()` accepts a configuration callback. `AutoCreate.All` auto-creates tables/fields.
- `LightweightSession()` opens a session without identity-map tracking (fastest path).
- `Store()` marks an entity for persistence on the next `SaveChangesAsync()`.
- `Query<T>()` combined with LINQ operators returns typed results.
- SurrealDB auto-assigns a record ID if none is set.

## See Also

- [Configuration](/getting-started/configuration/)
- [Example: Configuration](/examples/configuration/)
