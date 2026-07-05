---
title: Run a LINQ Query
description: Filter, sort, and project with type-safe LINQ expressions
---

## Goal

Query active users registered in the last 30 days, return a projection.

## Code

```csharp
using AeroDB;

await using var session = store.QuerySession();

var thirtyDaysAgo = DateTimeOffset.UtcNow.AddDays(-30);

var users = await session.Query<User>()
    .Where(u => u.Active && u.CreatedAt > thirtyDaysAgo)
    .OrderByDescending(u => u.LastLogin)
    .Select(u => new { u.Id, u.Name, u.Email })
    .Take(50)
    .ToListAsync();
```

## Explanation

- `QuerySession()` opens a read-only session (no change tracking overhead).
- LINQ operators — `Where`, `OrderByDescending`, `Select`, `Take` — are translated to SurrealQL.
- `ToListAsync()` materializes the query asynchronously.
- Complex boolean expressions (`Active && CreatedAt > ...`) are translated faithfully.
- Type-safe: renaming `Name` to `FullName` would break at compile time.

## Also Works

```csharp
// Count
var count = await session.Query<User>().CountAsync(u => u.Active);

// Single
var alice = await session.Query<User>()
    .FirstOrDefaultAsync(u => u.Email == "alice@example.com");

// Paging
var page = await session.Query<User>()
    .Where(u => u.Active)
    .Skip(20).Take(20)
    .ToListAsync();
```

## See Also

- [Querying](/guides/querying/)
- [Example: Querying](/examples/querying/)
