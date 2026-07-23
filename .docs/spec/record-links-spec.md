# Record Link Support in Dali — Specification

> SurrealDB's native relationship via `TYPE record<T>` fields (equivalent to foreign keys in SQL).
> Also covers 1-to-many child records via subquery reverse include.

## 1. Motivation

SurrealDB has two relationship models:

| Model | Syntax | Dali Status |
|-------|--------|-------------|
| **Graph edges** | `(person)->knows->(person)` | ✅ `GraphQueryBuilder` |
| **Record links** | `TYPE record<customer>` on a field | ❌ **Not yet supported** |

Record links are SurrealDB's equivalent of SQL foreign keys. A field like `customer` typed `record<customer>` stores a direct record reference (`customer:alice`) that SurrealDB can **dot-walk** in queries — no JOIN needed.

## 2. Renaming

| Before | After | Reason |
|--------|-------|--------|
| `.Include()` (LET-based batch) | `.IncludeBatch()` | Frees `.Include()` for record links |
| `.Fetch()` | Unchanged | Already handles forward record links |
| `.Include()` new | `.Include()` | Subquery-based record link loading |
| (new) | `.IncludeReverse()` | 1-to-many child records |
| (new) | `.FilterInclude()` | Explicit wrapper for filtering on included data |

## 3. SurrealDB Verification Results

All patterns tested on SurrealDB MCP server:

### 3a. 1-to-Many (Subquery)

```surql
-- Returns items as nested array inside each order
SELECT *, (SELECT * FROM order_item WHERE order = $parent.id) AS items FROM orders;
```

| Subquery Feature | Result |
|------------------|--------|
| `ORDER BY price DESC` inside subquery | ✅ Works per-parent |
| `LIMIT N` inside subquery | ✅ Works per-parent |
| Computed fields: `(quantity * price) AS line_total` | ✅ Per-row expressions |
| `count()` aggregate | ✅ Works |
| `math::sum(quantity * price)` | ⚠️ Needs `GROUP ALL` wrapping |
| Same-level WHERE on subquery | ❌ `WHERE items[0].price > 20` — subquery not yet evaluated |
| Outer wrapper filter | ✅ `SELECT * FROM (...) WHERE items[0].price > 20` |

### 3b. Dot-walk

```surql
SELECT customer.name, customer.email FROM orders;  -- ✅ works natively
```

### 3c. FETCH (forward record expansion)

```surql
SELECT * FROM orders FETCH customer;  -- ✅ server-side expand, but CBOR deserialization gap
```

## 4. Architecture Decision: Single Round-Trip Subquery

**Adopted: Single round-trip subquery.** Not multi-query.

Rationale:
- Subqueries support ORDER BY, LIMIT, computed fields, and aggregates inside them
- Complex filtering via a wrapper SELECT (two layers, one round-trip)
- Multi-query introduces inconsistency windows, connection overhead, N+1 patterns — unacceptable for high-load SaaS
- Forward `record<T>` links already work via `FETCH` — just need deserialization fix
- The existing `.Include()` (now `.IncludeBatch()`) remains available for LET-based batch loading

## 5. The Three Existing Mechanisms

Before building new features, understand what already exists:

| API | Mechanism | Direction | Status |
|-----|-----------|-----------|--------|
| `.Fetch()` | `SELECT ... FETCH customer` — **server-side** expansion of `record<T>` | Forward only | ✅ Exists, ❌ deserialization gap |
| `.IncludeBatch()` | `LET $ids = (...)` — **client-side** batch load + callback dispatch (formerly `.Include()`) | Forward only | ✅ Exists (renamed) |
| (new) `.Include()` | `(SELECT * FROM target WHERE id = $parent.fk LIMIT 1) AS _name` — **server-side subquery** | Both directions | ❌ New |

## 6. API Design

### 6a. Forward Include (subquery, renamed `.Include()`)

```csharp
session.Query<Order>()
    .Include(o => o.Customer)  // detects record<customer>, generates subquery
    .FirstAsync(o => o.Id == "101");
```

Generated:
```surql
SELECT *, (SELECT * FROM customer WHERE id = $parent.customer LIMIT 1) AS _customer
FROM orders WHERE id = orders:o1 LIMIT 1;
```

### 6b. Forward Include via FETCH (existing, fix deserialization)

```csharp
session.Query<Order>()
    .Fetch(o => o.Customer)  // already works, just needs CBOR deserialization fix
    .FirstAsync(o => o.Id == "101");
```

Generated:
```surql
SELECT * FROM orders FETCH customer
WHERE id = orders:o1 LIMIT 1;
```

### 6c. Reverse Include — 1-to-Many

```csharp
session.Query<Order>()
    .IncludeReverse(o => o.Items, "order_item", foreignKey: "order")
    .FirstAsync(o => o.Id == "101");
```

Generated:
```surql
SELECT *, (SELECT * FROM order_item WHERE order = $parent.id) AS _items
FROM orders WHERE id = orders:o1 LIMIT 1;
```

### 6d. Reverse Include with ordering/limits

```csharp
session.Query<Order>()
    .IncludeReverse(o => o.Items, "order_item", "order",
        items => items.OrderByDescending(i => i.Price).Take(5))
    .FirstAsync(o => o.Id == "101");
```

Generated:
```surql
SELECT *, (
  SELECT * FROM order_item WHERE order = $parent.id ORDER BY price DESC LIMIT 5
) AS _items
FROM orders WHERE id = orders:o1 LIMIT 1;
```

### 6e. FilterInclude — Explicit Wrapper

```csharp
session.Query<Order>()
    .IncludeReverse(o => o.Items, "order_item", "order")
    .FilterInclude(o => o.Items, items => items.Any(i => i.Price > 50))
    .ToListAsync();
```

Generated:
```surql
SELECT * FROM (
  SELECT *, (SELECT * FROM order_item WHERE order = $parent.id) AS _items
  FROM orders
)
WHERE _items[0].price > 50;
```

**.Where() vs .FilterInclude():** `.Where()` always filters the root table. `.FilterInclude()` explicitly generates a wrapper query to filter on included data. No auto-detection magic.

### 6f. Dot-walk in Select

```csharp
session.Query<Order>()
    .Select(o => new OrderDto {
        CustomerName = o.Customer.Name,
        CustomerEmail = o.Customer.Email
    })
    .ToListAsync();
```

Generated:
```surql
SELECT customer.name AS CustomerName, customer.email AS CustomerEmail FROM orders;
```

## 7. Implementation Phases

### Phase 1: Rename `.Include()` → `.IncludeBatch()`

Simple find-and-replace across the codebase and tests. No behavior change.

**Files:** `ISableQueryable.cs`, `SurrealQueryProvider.cs`, all test files referencing `.Include(`
**Tests:** 0 (compilation check only)

---

### Phase 2: Fix Fetch Deserialization

The existing `.Fetch()` generates correct `SELECT ... FETCH customer` SurrealQL. The CBOR response contains the expanded record as a nested object, but `SurrealQueryProvider.DeserializeMainAndIncludes` doesn't hydrate it into the C# property.

**Fix:** In the deserialization phase, detect `FETCH` fields and map CBOR-nested structures into the target property types.

**Files:** `SurrealQueryProvider.cs`
**Tests:** ~4 (single record link, multiple links, null link, multiple `FETCH`)

---

### Phase 3: Forward Include (subquery-based `.Include()`)

**New files:** `IncludeExtensions.cs`, `IncludeQueryGenerator.cs`
**Modified:** `SurrealQueryResult.cs`, `SurrealQueryProvider.cs`

`IncludeExtensions.cs`:
```csharp
public static ISableQueryable<T> Include<T, TInclude>(
    this ISableQueryable<T> source,
    Expression<Func<T, TInclude?>> property)
    where TInclude : class
```

**Deserialization:** Two-phase CBOR post-processing:
1. Extract `_includeName` arrays from each CBOR row
2. For single `TInclude` property: take `result[0]` or `null`; log warning if `Count > 1`
3. For `List<TInclude>` property: take the full array

**SurrealQL:** Append `LIMIT 1` for single-valued includes:
```surql
-- Forward: has LIMIT 1 because it's a single record<T>
(SELECT * FROM customer WHERE id = $parent.customer LIMIT 1) AS _customer

-- Reverse: no LIMIT 1 because it's a collection
(SELECT * FROM order_item WHERE order = $parent.id) AS _items
```

**Tests:** ~6 (single, multiple, null link, with Where, with OrderBy, LIMIT 1 generation)

---

### Phase 4: Reverse Include (`.IncludeReverse()`)

**Files:** `IncludeExtensions.cs`, `IncludeQueryGenerator.cs`

```csharp
public static ISableQueryable<T> IncludeReverse<T, TChild>(
    this ISableQueryable<T> source,
    Expression<Func<T, IEnumerable<TChild>>> property,
    string childTable,
    string foreignKey)
    where TChild : class
```

The user must specify:
- `childTable`: the SurrealDB table name for the child records
- `foreignKey`: the field on the child table that references this parent (e.g., `"order"`)

Optional ordering: delegate that configures `AggregateQueryBuilder` for `ORDER BY` / `LIMIT` inside the subquery.

**Tests:** ~6 (basic, with sort, with limit, empty child set, multi-child, null-safe)

---

### Phase 5: FilterInclude — Explicit Wrapper

**Files:** `IncludeExtensions.cs`, `IncludeQueryGenerator.cs`

```csharp
public static ISableQueryable<T> FilterInclude<T, TChild>(
    this ISableQueryable<T> source,
    Expression<Func<T, IEnumerable<TChild>>> property,
    Expression<Func<IEnumerable<TChild>, bool>> filter)
    where TChild : class
```

Generates a wrapping `SELECT * FROM (...) WHERE condition` on the outer level.

The key insight from MCP verification: `WHERE items[0].price > 50` only works in an *outer* wrapper, not in the same-level SELECT. This is why `FilterInclude()` exists as an explicit method.

**Tests:** ~5 (basic, combined includes, nested, empty filtered set, multiple filters)

---

### Phase 6: Dot-walk in Select Projections

**Files:** `ExpressionVisitor.cs`

Modify `TranslateMethod` / `VisitMember` to detect chained `MemberExpression` paths (`o.Customer.Name` → `customer.name`):

```csharp
// Inside TranslateMethod or the member visit in TranslateCondition:
if (node.Expression is MemberExpression inner)
{
    // Chained: o.Customer.Name → "customer.name"
    var parts = new List<string>();
    var current = node;
    while (current.Expression is MemberExpression me)
    {
        parts.Insert(0, me.Member.Name);
        current = me;
    }
    parts.Insert(0, current.Member.Name);
    // Wait — the first part is the parameter access (o), skip it
    return string.Join(".", parts.Select(Snake));
}
```

Actually, the approach needs to be more careful. The existing `ProjMember()` at ExpressionVisitor.cs:384 handles `x => x.Customer` → `"Customer"`. For `x => x.Customer.Name`, the expression tree is:

```
MemberExpression: Name
  ├── MemberExpression: Customer
  │     └── ParameterExpression: x
```

So the algorithm is:
1. In `ProjMember()`, detect if the member expression has an inner member expression (not parameter)
2. Walk up the chain, collecting member names
3. Join with "." and lowercase

**Tests:** ~5 (basic, two-level, three-level, missing link, with Convert)

---

### Phase 7: Integration + Composition

**Tests:** ~8

- Combine `.Include()` + `.Fetch()` + `.IncludeReverse()` + `.FilterInclude()`
- Composition with `.Where()`, `.OrderBy()`, `.Take()`, `.Skip()`
- Nested includes (2-3 levels)
- Error handling: invalid foreign table, missing records, CBOR type mismatches
- Ensure no regression on existing `.IncludeBatch()` behavior

## 8. Deserialization Strategy

The subquery results come back as nested CBOR arrays inside the parent document objects:

```json
{
  "id": "orders:o1",
  "status": "pending",
  "_customer": [{ "name": "Alice", "email": "alice@test.com" }],
  "_items": [
    { "product_name": "Widget", "quantity": 2, "price": 9.99 },
    { "product_name": "Gadget", "quantity": 1, "price": 24.99 }
  ]
}
```

**Two-phase approach:**

Phase A: Deserialize main result row as untyped `Dictionary<string, object?>` (or use existing CBOR deserialization)

Phase B: Extract `_includeName` prefixed keys:
- `_customer` → deserialize from `List<object>` → `List<Customer>` → take `[0]` for single `Customer` property
- `_items` → deserialize from `List<object>` → `List<OrderItem>` → set `Order.Items` directly

Phase C: Map extracted include results onto the target C# object using reflection (`PropertyInfo.SetValue`)

For single-valued forward includes with `LIMIT 1`:
- 0 results → default/null
- 1 result → correct
- >1 results → log `Debug` warning (schema integrity concern), take `[0]`

## 9. Limitations

| Limitation | Reason | Workaround |
|------------|--------|------------|
| 3-level max nesting | Query complexity | Separate queries for deeper graphs |
| `$parent` one-level deep | SurrealDB limitation | Flatten nested includes in query generator |
| No pagination on included data per parent | SurrealDB subquery semantics | Use a separate query with explicit WHERE + LIMIT |
| Forward include always returns an array | SurrealDB SELECT semantics | Post-processing unwraps single-object arrays |
| `math::sum(qty * price)` in subquery | Per-row scalar issue | Use `GROUP ALL` or wrap in nested `(SELECT math::sum(val) FROM ...)` |

## 10. Test Plan (~34)

| Phase | Group | Count |
|-------|-------|-------|
| 1 | Rename IncludeBatch | 0 (compilation) |
| 2 | Fetch deserialization | 4 |
| 3 | Forward Include | 6 |
| 4 | Reverse Include 1:M | 6 |
| 5 | FilterInclude | 5 |
| 6 | Dot-walk Select | 5 |
| 7 | Integration + Composition | 8 |
| **Total** | | **34** |

## 11. File Map

| Action | File |
|--------|------|
| Rename | `ISableQueryable.cs` — `.Include()` → `.IncludeBatch()` |
| Rename | `SurrealQueryProvider.cs` — all `IncludeDescriptor` refs → `IncludeBatchDescriptor` |
| Rename | All test files — `.Include(` → `.IncludeBatch(` |
| Modify | `ExpressionVisitor.cs` — `ProjMember()` / `VisitMember` dot-walk |
| Modify | `SurrealQueryProvider.cs` — FETCH deserialization, subquery include execution |
| Modify | `SurrealQueryResult.cs` — `Includes`, `ReverseIncludes`, `RequiresWrapper` |
| **New** | `IncludeExtensions.cs` — `.Include()`, `.IncludeReverse()`, `.FilterInclude()` |
| **New** | `IncludeQueryGenerator.cs` — sub-SELECT, wrapper generation |

## 12. Note on Existing `.IncludeBatch()` (formerly `.Include()`)

The old `.Include()` will be renamed to `.IncludeBatch()`.

Before:
```csharp
session.Query<Order>()
    .Include(x => x.CustomerId, (Customer c) => { order.Customer = c; })
    .ToListAsync();
```

After (rename only):
```csharp
session.Query<Order>()
    .IncludeBatch(x => x.CustomerId, (Customer c) => { order.Customer = c; })
    .ToListAsync();
```

Behavior unchanged: single round-trip via `LET $ids = (...); SELECT * FROM target WHERE id IN $ids;`.
