---
title: LINQ
description: Type-safe LINQ queries
---

AeroDB's LINQ provider translates C# expressions to efficient SurrealQL. All queries are parameterized by default to prevent injection.

## Supported LINQ Operations

| Operator | Example | Notes |
|----------|---------|-------|
| `Where` | `.Where(u => u.Age > 18)` | Supports `&&`, `\|\|`, `!` |
| `Select` | `.Select(u => new { u.Name })` | Projections to DTOs |
| `OrderBy` / `ThenBy` | `.OrderBy(u => u.Name).ThenBy(u => u.Age)` | Also `Descending` |
| `Skip` / `Take` | `.Skip(10).Take(20)` | Pagination |
| `Count` / `LongCount` | `.CountAsync(u => u.IsActive)` | Conditional overload |
| `Any` / `All` | `.AnyAsync(u => u.Email == "x")` | Short-circuit |
| `Sum` / `Average` | `.AverageAsync(u => u.Score)` | Numeric only |
| `Min` / `Max` | `.MaxAsync(u => u.Price)` | Supports Select |
| `First` / `FirstOrDefault` | `.FirstOrDefaultAsync(u => u.Email == "x")` | Throws on none (First) |
| `Single` / `SingleOrDefault` | `.SingleAsync(u => u.Id == id)` | Throws on multiple |
| `Contains` | `.Where(u => ids.Contains(u.Id))` | Translated to `IN` |
| `StartsWith` / `EndsWith` | `.Where(u => u.Name.StartsWith("A"))` | String matching |
| `Include` / `ThenInclude` | `.Include(o => o.Customer)` | Eager loading |
| `GroupBy` | `.GroupBy(u => u.Dept)` | Aggregations |
| `Distinct` | `.Select(u => u.City).Distinct()` | Dedup |

## Expression Translation to SurrealQL

The provider walks the expression tree and maps to SurrealQL:

| C# | SurrealQL |
|----|-----------|
| `u.Name == "Alice"` | `name = 'Alice'` |
| `u.Age > 18 && u.Status == "active"` | `age > 18 AND status = 'active'` |
| `u.Name.StartsWith("A")` | `name STARTSWITH 'A'` |
| `ids.Contains(u.Id)` | `id IN $ids` |
| `u.CreatedAt >= DateTime.Today` | `created_at >= d'2025-07-05'` |

## Parameterized Queries

All values are parameterized automatically. Access generated parameters for diagnostics:

```csharp
var query = session.Query<User>()
    .Where(u => u.Age > minAge && u.Status == status);

var (surrealql, parameters) = query.ToDebugString();
// SELECT * FROM user WHERE age > $p0 AND status = $p1
```

## Projections to DTOs

Select only the fields you need for better performance:

```csharp
var dtos = await session.Query<User>()
    .Where(u => u.Department == "Engineering")
    .Select(u => new UserDto
    {
        Id = u.Id,
        FullName = u.FirstName + " " + u.LastName,
        Email = u.Email
    })
    .ToListAsync();
```

## Compiled Queries for Hot Paths

Avoid re-compiling the same query shape repeatedly:

```csharp
private static readonly CompiledQuery<User, User?> GetByEmail =
    CompiledQuery.Create((IQueryable<User> q, string email) =>
        q.FirstOrDefaultAsync(u => u.Email == email));

// Usage — cache and reuse
var user = await GetByEmail(session.Query<User>(), "alice@example.com");
```

## Limitations and Workarounds

| Limitation | Workaround |
|------------|------------|
| No `GroupJoin` / `Join` | Use `Include` or raw SurrealQL |
| No `let` expressions | Chain `.Select` with intermediate DTOs |
| Complex math (trig, log) | Use raw SurrealQL with `RawQueryAsync` |
| Recursive CTEs | Use `Graph<T>()` API or raw SurrealQL |
| `String.Format` / interpolation | Use string concatenation `+` |
| `Enum.HasFlag` | Compare bitwise with `(flag & value) != 0` |

For unsupported operations, fall back to raw SurrealQL via `RawQueryAsync<T>`.
