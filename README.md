# Dali - SurrealDB EF Core LINQ Provider

A complete example of building an EF Core–style LINQ provider for SurrealDB that transparently handles **document**, **graph**, and **raw SQL** query modes.

---

## Architecture

```
Your LINQ Query
      │
      ▼
SurrealQueryable<T>          ← IQueryable<T> + IAsyncEnumerable<T>
      │
      ▼
SurrealQueryProvider          ← IQueryProvider — routing + async helpers
      │
      ▼
SurrealExpressionVisitor      ← Walks Expression tree → SurrealQL string
      │   ├── Document mode:  WHERE / ORDER BY / LIMIT / START
      │   ├── Graph mode:     ->edge->target traversal paths
      │   └── SQL mode:       raw SurrealQL pass-through
      ▼
TranslatedQuery               ← (SurrealQL string, QueryMode, ElementType)
      │
      ▼
SurrealHttpExecutor           ← POST /sql → parse [{ status, result }]
      │
      ▼
SurrealDB (HTTP API)
```

---

## Query Modes

### 1. Document Mode
Standard LINQ → SurrealQL SELECT:

```csharp
var adults = await ctx.People
    .Where(p => p.Age >= 18 && p.Name.Contains("A"))
    .OrderBy(p => p.Name)
    .Skip(20).Take(10)
    .ToListAsync();
// → SELECT * FROM person
//   WHERE (age >= 18) AND (string::contains(name, 'A'))
//   ORDER BY name ASC LIMIT 10 START 20
```

### 2. Graph Mode
Edge creation and traversal:

```csharp
// RELATE
await ctx.Graph.RelateAsync<Knows>("person:alice", "person:bob",
    new Knows { Strength = 0.9 });
// → RELATE person:alice->knows->person:bob CONTENT {...}

// Traverse out
var friends = await ctx.Graph.TraverseOutAsync<Knows, Person>("person:alice");
// → SELECT ->knows->person.* FROM person:alice

// LINQ-style traverse
var result = await ctx.People
    .Where(p => p.Name == "Alice")
    .Traverse<Knows, Person>("out")
    .Where(p => p.Age > 18)
    .ToListAsync();
// → SELECT ->knows->person.* FROM person
//   WHERE name = 'Alice' AND age > 18
```

### 3. SQL / Analytic Mode
Full SurrealQL pass-through:

```csharp
var stats = await ctx.QueryAsync<...>("""
    SELECT math::mean(age) AS avg, count() AS total
    FROM person GROUP ALL;
""");

// Or escape hatch inside LINQ:
var vips = await ctx.People
    .Where(p => p.Age > 18)
    .Raw("tags CONTAINS 'vip'")
    .ToListAsync();
```

---

## Supported LINQ Operators

| LINQ              | SurrealQL                        |
|-------------------|----------------------------------|
| `.Where()`        | `WHERE field op value`           |
| `.Select()`       | `SELECT field1, field2`          |
| `.OrderBy()`      | `ORDER BY field ASC`             |
| `.OrderByDescending()` | `ORDER BY field DESC`       |
| `.Skip(n)`        | `START n`                        |
| `.Take(n)`        | `LIMIT n`                        |
| `&&` / `\|\|`     | `AND` / `OR`                     |
| `!`               | `NOT`                            |
| `.Contains(str)`  | `string::contains(field, val)`   |
| `.StartsWith()`   | `string::startsWith(field, val)` |
| `list.Contains()` | `field CONTAINS val`             |
| `.Traverse<E,T>()` | `->edge->target.*`             |
| `.Raw(surql)`     | Injected verbatim into WHERE     |

---

## Key Files

| File | Purpose |
|------|---------|
| `Core/SurrealModels.cs` | `RecordId`, `SurrealEntity`, `SurrealEdge<TIn,TOut>`, domain models |
| `Query/ExpressionVisitor.cs` | Walks LINQ trees → SurrealQL strings |
| `Infrastructure/QueryProvider.cs` | `IQueryProvider`, `SurrealQueryable<T>`, `SurrealDbSet<T>` |
| `Infrastructure/SurrealContext.cs` | DbContext equivalent, `SurrealGraphApi`, HTTP executor |
| `Extensions/Extensions.cs` | `ToListAsync`, `Traverse<>`, `Raw()`, DI registration |
| `Examples.cs` | Full usage examples for all three modes |

---

## Production Considerations

- **Connection pooling** — reuse `HttpClient` via `IHttpClientFactory`
- **Parameter binding** — replace string interpolation with `$param` placeholders to prevent SurrealQL injection
- **Schema migration** — wrap `DEFINE TABLE / FIELD / INDEX` calls in versioned migration scripts
- **Live queries** — use WebSocket client for `LIVE SELECT` subscriptions
- **Auth** — swap Basic auth for JWT via `SIGNIN` / `AUTHENTICATE`
- **Caching** — add result caching at the `ISurrealQueryExecutor` layer
