---
title: Querying
description: Fluent LINQ and raw SurrealQL queries
---

AeroDB translates LINQ expressions directly to optimized SurrealQL. You can also drop down to raw SurrealQL when needed.

## Fluent LINQ

```csharp
// Type-safe LINQ queries
var activeUsers = await session.Query<User>()
    .Where(u => u.Active && u.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30))
    .OrderByDescending(u => u.LastLogin)
    .Take(10)
    .ToListAsync();

// Join / multi-table
var results = await session.Query<Order>()
    .Where(o => o.Status == "pending")
    .SelectMany(o => o.Items)
    .ToListAsync();
```

## Raw SurrealQL

```csharp
// Execute raw SurrealQL with parameters and type-safe result mapping
var users = await session.RawQueryAsync<User>(
    "SELECT * FROM user WHERE age > $minAge",
    new Dictionary<string, object?> { ["minAge"] = 21 }
);

// Execute a non-query SurrealQL statement (CREATE, UPDATE, DELETE, DEFINE)
await session.ExecuteSqlAsync(
    "CREATE person:john SET name = 'John', age = 30"
);
```

**Full sample:** [`samples/DocSamples/`](https://github.com/microbian-systems/AeroDB/tree/main/samples/DocSamples)
