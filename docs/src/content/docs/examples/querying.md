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
// Direct SurrealQL with type-safe result mapping
var users = await session.Query<User>()
    .WithSQL("SELECT * FROM user WHERE age > $minAge")
    .With("minAge", 21)
    .ToListAsync();

// Execute arbitrary SurrealQL
var result = await session.ExecuteAsync(
    "CREATE person:john SET name = 'John', age = 30"
);
```

**Full sample:** [`samples/DocSamples/`](https://github.com/microbians/AeroDB/tree/main/samples/DocSamples)
