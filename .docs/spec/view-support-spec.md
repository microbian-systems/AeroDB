# View Support Spec — Pre-Computed Table Views for Dali

**Status:** Draft spec (pre-implementation)
**Target:** Dali core library
**Date:** 2026-06-22
**Docs:** [`architecture.md`](architecture.md)

---

## 1. SurrealDB's View Mechanism

SurrealDB defines views as **materialized, incrementally-updating tables** backed by a `SELECT` query. They are NOT standard SQL "bookmarked" views — they store results and update them incrementally via event triggers.

```surql
DEFINE TABLE IF NOT EXISTS avg_product_review AS
SELECT
  count() AS number_of_reviews,
  math::mean(<float> rating) AS avg_review,
  ->product.id AS product_id,
  ->product.name AS product_name
FROM review
GROUP BY product_id, product_name;
```

**Key properties** (from SurrealDB docs):
- **Event-based**: changes to the `FROM` table trigger incremental updates
- **Materialized**: first run computes and persists the result set
- **Incrementally updating**: subsequent changes apply deltas, not full recompute
- **Graph traversal supported**: `->edge->field` works inside the SELECT
- **Limitation**: trigger only fires on the `FROM` table, not on referenced tables (e.g., `->product.id` changes won't trigger view refresh)

Additional SurrealQL patterns:
```surql
-- Simple view with filter
DEFINE TABLE IF NOT EXISTS active_users AS
SELECT * FROM user WHERE active = true;

-- Aggregate view with graph traversal
DEFINE TABLE IF NOT EXISTS user_stats AS
SELECT
  id,
  ->wrote_post AS post_count,
  math::mean(<-written_by->rating.value) AS avg_rating
FROM user;

-- Drop table (data processed by trigger, then deleted)
DEFINE TABLE logs DROP;
```

---

## 2. Dali's Existing Features That Compose Into Views

| Existing component | Reusable for views? | Notes |
|-------------------|--------------------|-------|
| **`SurrealExpressionVisitor`** — LINQ → SurrealQL | ✅ WHERE predicates | Already handles >, >=, <, <=, ==, !=, AND, OR, NOT, string.Contains, string.StartsWith, DateTime.Year/Month |
| **`SurrealExpressionVisitor` — `.Select()` projection** | ✅ Column selection + aliases | `NewExpression` → `col AS alias`, `MemberInitExpression` → `col AS alias` |
| **`SurrealQueryResult.ToSurrealQL()`** — SELECT structure | ✅ SELECT + FROM + WHERE + ORDER BY + GROUP ALL | Generates the SELECT shell needed for views |
| **`GraphSurrealQLGenerator`** — graph traversal | ✅ `->edge->field` syntax | Generates `->product.id` style expressions in SELECT columns |
| **`SchemaManager.EnsureTableAsync()` / `DefineTableAsync()`** | ✅ Schema registration | Already emits `DEFINE TABLE ... SCHEMAFULL` — needs `AS SELECT` variant |
| **Aggregates: `Count`, `Sum`, `Min`, `Max`, `Average`** | ✅ `count()`, `math::sum()`, etc. | Already translates via `SurrealQueryProvider` aggregates |
| **`.Include()`** | ❌ Not reusable | Uses `LET $main` + subquery — single SELECT required |
| **`.Fetch()`** | ❌ Not reusable | Post-query expansion, not part of SELECT definition |

**Missing pieces that need new code:**
- `DEFINE TABLE ... AS SELECT ...` SQL generation (new clause on SchemaManager)
- View registration on `StoreOptions`
- GROUP BY column translation in ExpressionVisitor (currently only `GroupAll` is supported)
- Graph traversal embedding within a SELECT column expression (currently standalone query)
- View initialization during `DocumentStore.InitializeAsync`

---

## 3. Proposed API Surface

### 3.1 Configuration — `StoreOptions.Views`

```csharp
public class StoreOptions
{
    // Existing:
    public SchemaOptions Schema { get; } = new();
    public EventSourcingOptions Events { get; } = new();
    public FunctionOptions Functions { get; } = new();
    
    // New:
    public ViewOptions Views { get; } = new();      // <--- ADD
}
```

### 3.2 View Definition — Fluent Builder

```csharp
// Minimal: SELECT FROM WHERE
o.Views.For<ActiveUser>("active_users")
    .From<User>()
    .Where(u => u.Active);

// → DEFINE TABLE active_users AS SELECT * FROM user WHERE Active = true;

// With projection + aggregate + graph
o.Views.For<AvgProductReview>("avg_product_review")
    .From<Review>()
    .Select(r => new {
        Count = r.Count(),
        AvgReview = r.Average(x => x.Rating),
        ProductId = r.Graph().Out<IProduct>("product").Select(p => p.Id),
        ProductName = r.Graph().Out<IProduct>("product").Select(p => p.Name)
    })
    .GroupBy(r => r.ProductId);

// → DEFINE TABLE avg_product_review AS
//   SELECT count() AS Count, math::mean(rating) AS AvgReview,
//          ->product.id AS ProductId, ->product.name AS ProductName
//   FROM review GROUP BY ProductId;

// DROP table (event-processed, auto-deleted)
o.Views.For<LogEntry>("logs")
    .From<EventLog>()
    .Drop();
// → DEFINE TABLE logs DROP;
//   (plus a DEFINE EVENT that processes and deletes the record)
```

### 3.3 ViewDefinition<T> Class

```csharp
public class ViewDefinition<T> where T : class
{
    public string ViewName { get; }
    public string? FromTable { get; private set; }
    public string? SelectSurql { get; private set; }
    public string? WhereSurql { get; private set; }
    public string? GroupBySurql { get; private set; }
    public bool IsDrop { get; private set; }
    
    internal ViewDefinition(string viewName);
    
    public ViewDefinition<T> From<TFrom>() where TFrom : class;
    public ViewDefinition<T> Select(Func<ViewSelector<T>, object> projection);
    public ViewDefinition<T> Where(Expression<Func<T, bool>> predicate);
    public ViewDefinition<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);
    public ViewDefinition<T> Drop();
}
```

### 3.4 ViewSelector<T> — Aggregate + Graph Helper

```csharp
public class ViewSelector<T>
{
    public long Count();
    public decimal Sum(Expression<Func<T, decimal>> field);
    public decimal Min(Expression<Func<T, decimal>> field);
    public decimal Max(Expression<Func<T, decimal>> field);
    public decimal Average(Expression<Func<T, decimal>> field);
    
    // Graph traversal embedded in SELECT (returns column expression string)
    // Usage: r.Graph().Out<Review>("wrote_post").Select(p => p.Id)
    public ViewGraphSelector<T> Graph();
}

public class ViewGraphSelector<T>
{
    public ViewGraphSelector<T> Out<TTarget>(string edgeType) where TTarget : class;
    public ViewGraphSelector<T> In<TTarget>(string edgeType) where TTarget : class;
    public string Select(Expression<Func<TTarget, object?>> field);
}
```

### 3.5 SchemaManager — View SQL Generation

```csharp
public class SchemaManager
{
    // Existing:
    public Task EnsureTableAsync(...);
    public Task EnsureDocumentSchemaAsync<T>(...);
    
    // New:
    public Task EnsureViewAsync<T>(
        ISurrealDbSession session,
        ViewDefinition<T> view,
        CancellationToken ct = default);
}

// Generated SurrealQL:
// DEFINE TABLE IF NOT EXISTS view_name AS
//   SELECT count() AS count, math::mean(rating) AS avg_rating, ->product.id AS product_id
//   FROM source_table
//   WHERE Active = true
//   GROUP BY product_id;
```

---

## 4. Reuse of Existing Components

### 4.1 ExpressionVisitor — SELECT + WHERE + GROUP BY

The `ExpressionVisitor` currently handles these `VisitMethodCall` cases:

| Method | Current status | Used for views? |
|--------|---------------|-----------------|
| `Where` | ✅ Translates condition to SurrealQL | ✅ Yes — view WHERE clause |
| `Select` | ✅ Translates projections with aliases | ✅ Yes — view SELECT columns |
| `OrderBy` / `ThenBy` | ✅ ORDER BY translation | ✅ Yes — view ORDER BY |
| `GroupBy` | ❌ Returns empty / GroupAll only | ❌ **Needs implementation** |
| `Sum` / `Min` / `Max` / `Average` | ✅ Aggregate name mapping | ✅ Yes — via ViewSelector helper |
| `Count` | ✅ (via CountAsync path) | ✅ Yes — `count()` |

**Work needed for GROUP BY:**
```csharp
// Current: Type GroupBy returns GroupAll
case "GroupBy":
    _groupAll = true;
    // No column tracking
    break;

// Needed: Track group-by columns
case "GroupBy":
    var keySelector = StripQuote(node.Arguments[1]) as LambdaExpression;
    if (keySelector?.Body is MemberExpression keyMember)
        _groupByColumns.Add(keyMember.Member.Name);
    // For anonymous type keys (e.g., x => new { x.Category, x.Region })
    if (keySelector?.Body is NewExpression newKey)
        foreach (var arg in newKey.Arguments)
            if (arg is MemberExpression m)
                _groupByColumns.Add(m.Member.Name);
    break;
```

### 4.2 GraphSurrealQLGenerator — In-Column Graph Traversal

Currently `GraphSurrealQLGenerator.Generate()` produces standalone queries:
```surql
SELECT ->product.* FROM review;
```

For views, graph traversal appears in SELECT columns:
```surql
SELECT ->product.id AS ProductId, ->product.name AS ProductName FROM review;
```

**Work needed:** Extract the column-level graph traversal from `GraphSurrealQLGenerator` into a reusable method:
```csharp
// New method: Generate column expression for graph traversal + field access
public static string GenerateColumnExpression(GraphQueryPlan plan, string fieldName)
{
    var path = BuildPathExpression(plan);
    return $"{path}.{fieldName}";
}
```

### 4.3 SchemaManager — DEFINE TABLE ... AS SELECT

The existing `SchemaManager` already handles `DEFINE TABLE`, `DEFINE FIELD`, `DEFINE INDEX`. Needs a new method:

```csharp
public Task EnsureViewAsync<T>(ISurrealDbSession session, ViewDefinition<T> view, CancellationToken ct)
{
    var surql = $"DEFINE TABLE IF NOT EXISTS {view.ViewName} AS {view.SelectSurql};";
    await session.RawQuery(surql, null, ct).ConfigureAwait(false);
}
```

Where `view.SelectSurql` is pre-built during configuration (not at execution time) using the ExpressionVisitor and GraphSurrealQLGenerator. This means:
- View SELECT statements are computed at `DocumentStore.InitializeAsync` time
- The `ViewDefinition<T>` stores the final SurrealQL string
- No parameters — view definitions are schema, not queries

### 4.4 Compiled Queries for Views

Views defined with `DEFINE TABLE ... AS SELECT` behave like tables at query time. No special support needed for querying — standard `session.Query<AvgProductReview>().Where(...).ToListAsync()` works because the view is just another table in SurrealDB.

---

## 5. Translation Examples

### 5.1 Simple Filtered View

```csharp
o.Views.For<ActiveUser>("active_users")
    .From<User>()
    .Where(u => u.Active && u.Role == "admin");
```

→ SurrealQL:
```surql
DEFINE TABLE IF NOT EXISTS active_users AS
SELECT * FROM user
WHERE (Active = true) AND (Role = 'admin');
```

Generated using existing `ExpressionVisitor.TranslateCondition()` via parameterized overload.

### 5.2 Aggregate View

```csharp
o.Views.For<AvgProductReview>("avg_product_review")
    .From<Review>()
    .Select(r => new {
        Count = r.Count(),
        Avg = r.Average(x => x.Rating),
        MinRating = r.Min(x => x.Rating)
    })
    .GroupBy(x => x.ProductId);
```

→ SurrealQL:
```surql
DEFINE TABLE IF NOT EXISTS avg_product_review AS
SELECT count() AS Count, math::mean(Rating) AS Avg, math::min(Rating) AS MinRating
FROM review
GROUP BY ProductId;
```

### 5.3 Graph Traversal View

```csharp
o.Views.For<PostWithAuthor>("post_with_author")
    .From<Post>()
    .Select(p => new {
        p.Title,
        AuthorName = p.Graph().In<Author>("wrote_post").Select(a => a.Name)
    });
```

→ SurrealQL:
```surql
DEFINE TABLE IF NOT EXISTS post_with_author AS
SELECT Title, <-wrote_post<-author.Name AS AuthorName
FROM post;
```

### 5.4 Drop Table (Event-Processed)

```csharp
o.Views.For<LogEntry>("logs")
    .From<EventLog>()
    .Drop();
```

→ SurrealQL:
```surql
DEFINE TABLE logs DROP;
```

---

## 6. Implementation Plan

### Phase 1 — Foundation (estimated: 1-2 days)

| Step | File | Effort |
|------|------|--------|
| 1. Add `ViewOptions` to `StoreOptions` | `src/Dali/StoreOptions.cs` | ~5 lines |
| 2. Create `ViewDefinition<T>` class | `src/Dali/Schema/ViewDefinition.cs` | ~80 lines |
| 3. Add `EnsureViewAsync()` to `SchemaManager` | `src/Dali/Schema/SchemaManager.cs` | ~30 lines |
| 4. Wire view initialization into `DocumentStore.InitializeAsync` | `src/Dali/DocumentStore.cs` | ~10 lines |
| 5. Built-in `InitializeAsync` iteration over `StoreOptions.Views.Definitions` | | |

### Phase 2 — SELECT + WHERE + GROUP BY (estimated: 1 day)

| Step | File | Effort |
|------|------|--------|
| 6. Add GROUP BY column tracking in `SurrealExpressionVisitor` | `src/Dali/Linq/ExpressionVisitor.cs` | ~20 lines |
| 7. Wire GROUP BY into `SurrealQueryResult` | `src/Dali/Linq/ExpressionVisitor.cs` | ~10 lines |
| 8. Translate `.Select()` projection into SurrealQL column list (mostly exists) | `ExpressionVisitor` | ~10 lines |
| 9. Combine into full SELECT string for view | `ViewDefinition.cs` (internal builder) | ~30 lines |

### Phase 3 — Graph Traversal in Columns (estimated: 0.5 day)

| Step | File | Effort |
|------|------|--------|
| 10. Extract reusable `GenerateColumnExpression` from `GraphSurrealQLGenerator` | `src/Dali/Graph/GraphSurrealQLGenerator.cs` | ~15 lines |
| 11. Wire graph column expressions into view SELECT | `ViewDefinition.cs` | ~15 lines |

### Phase 4 — Tests (estimated: 1 day)

| Step | File | Effort |
|------|------|--------|
| 12. Unit tests for SQL generation (all 4 patterns) | `tests/Dali.Tests/ViewTests.cs` | ~100 lines |
| 13. Integration test: create view, query it | `tests/Dali.Tests/ViewTests.cs` | ~60 lines |

### Phase 5 — Docs

| Step | File | Effort |
|------|------|--------|
| 14. Add view section to `.docs/design/architecture.md` | `.docs/design/architecture.md` | ~60 lines |

**Total estimated: ~450 lines new code, ~3-4 days**

---

## 7. Open Questions / Decisions Needed

### 7.1 Graph traversal in SELECT columns — API shape

The proposed API uses `.Graph().Out<X>("edge").Select(x => x.Field)`:
```csharp
r => new { Name = r.Graph().In<Person>("wrote").Select(p => p.Name) }
```

Alternative: use the existing `GraphQueryBuilder` syntax directly:
```csharp
r => new { Name = q.Out<Person>("wrote").Select(p => p.Name) }
```

**Decision needed**: Does `ViewSelector<T>.Graph()` return a lightweight graph builder for column-level traversal, or do we reuse the full `IGraphQuery<T>` interface?

**Recommendation**: Lightweight `ViewGraphSelector<T>` — the full `IGraphQuery<T>` has terminal operations (`.ToListAsync()`, `.Depth()`, `.ShortestPath()`) that don't apply to column expressions.

### 7.2 Parameterization of WHERE in view definitions

View WHERE clauses are defined at configuration time and are constant. They use the inlining path of `TranslateCondition(Expression)` since they're schema definitions, not user queries. This means:

```csharp
.Where(u => u.Role == "admin")
// → Produces WHERE Role = 'admin' (inlined, as schema is not user input)
```

**Decision**: Use the inlining overload for view WHERE clauses (same as SearchQuery), not the parameterized overload. This is correct — the view definition IS the schema.

### 7.3 DROP table syntax

`DEFINE TABLE ... DROP` creates a table that automatically deletes records after they're processed by triggers/events. This is useful for log/event sinks.

```csharp
o.Views.For<LogEntry>("api_logs").From<RawLog>().Drop();
```

**Decision**: `.Drop()` is a terminal modifier on the view definition. It's mutually exclusive with `.Select()` — you can't have both a SELECT query and DROP behavior.

### 7.4 SurrealQL GROUP BY syntax

SurrealDB uses `GROUP BY` differently from SQL:
```surql
SELECT count() AS num, category FROM product GROUP BY category;
-- SQL GROUP BY requires category in SELECT. SurrealDB is the same.
```

VS SQL's:
```sql
SELECT category, count(*) AS num FROM product GROUP BY category;
```

The `GROUP BY` columns must appear in the SELECT projection. The current `ExpressionVisitor.Translate()` handles this through the `.Select()` + `.GroupBy()` combination.

**Decision**: Require `.GroupBy()` columns to also appear in `.Select()` — same as SQL/SurrealQL semantics. The builder validates this at configuration time.
