# `BulkUpdateAsync` — Fluent Server-Side Bulk Update

## Motivation

Dali currently has `BulkInsertAsync<T>` and `BulkDeleteAsync<T>` for bulk write operations, but no bulk UPDATE. Operations like "move all due Scheduled messages to Incoming" in `DaliScheduledJobAgent` require raw SurrealQL:

```surql
UPDATE wolverine_incoming_envelopes SET status = 'Incoming', owner_id = 0
WHERE status = 'Scheduled' AND execution_time <= '2026-06-30T12:00:00Z'
```

A fluent API would avoid raw strings and automatically handle tenant scoping, table name resolution, and SurrealQL escaping.

## Design

### API Surface

Extension method on `ISurrealDbQueryable<T>`:

```csharp
public static async Task<int> BulkUpdateAsync<T>(
    this ISurrealDbQueryable<T> queryable,
    object updateValues,
    CancellationToken ct = default)
```

**Usage:**
```csharp
int affected = await session.Query<WolverineIncomingEnvelope>()
    .Where(e => e.Status == "Scheduled" && e.ExecutionTime <= now)
    .BulkUpdateAsync(new { Status = "Incoming", OwnerId = 0 }, ct);
```

### What It Generates

For Conjoined multi-tenancy with a `TenantId`-aware type:
```surql
UPDATE wolverine_incoming_envelopes
SET status = 'Incoming', owner_id = 0
WHERE TenantId = 'tenant-123'
  AND status = 'Scheduled'
  AND execution_time <= '2026-06-30T12:00:00Z'
```

For non-tenanted types:
```surql
UPDATE wolverine_incoming_envelopes
SET status = 'Incoming', owner_id = 0
WHERE status = 'Scheduled'
  AND execution_time <= '2026-06-30T12:00:00Z'
```

### Return Value

`int` — number of rows affected (from SurrealDB response). SurrealDB returns an array of updated records; the count is the array length.

## Implementation

### Location

- **Method**: `src/Dali/Linq/SurrealQueryExtensions.cs` (alongside existing query extensions)
- **Internal building**: `src/Dali/Storage/BulkOperations.cs` (alongside `BulkInsertAsync`)

### Algorithm

```
1. Resolve table name via DocTable<T>() (existing pattern)
2. Get the compiled SurrealQL from queryable.ToCommand()
   → This produces: SELECT * FROM {table} WHERE {tenantFilter} AND {whereClauses}
3. Transform SELECT → UPDATE, remove * after UPDATE
4. Build SET clause from updateValues anonymous object:
   - For each property: `{camelCaseName} = {surqlValue}`
   - SurrealQL value formatting: reuse existing FormatValue logic from expression visitor
   - Strings: quoted with single quotes, escaped
   - Numbers/booleans: direct
   - DateTime/DateTimeOffset: d'...' format
   - null: NONE
5. Assemble: UPDATE {table} SET {setClause} WHERE {whereClause}
6. Execute via session.RawQueryAsync(sql, ct)
7. Parse response count from FirstOk / GetValue
```

### Key Integration Points

| Component | Role |
|-----------|------|
| `ISurrealDbQueryable<T>.ToCommand()` | Produces the SELECT query with WHERE + tenant filters already applied |
| `DocTable<T>()` | Table name resolution (snake_case convention) |
| `ApplyTenantFilter()` (in `SurrealQueryProvider`) | Already built into the queryable pipeline — no extra tenant logic needed |
| `FormatValue()` (in `ExpressionVisitor`) | Reuse for literal value formatting in SET clause |
| `SurrealDbResponse.GetValue<>()` | Parse response row count |

## Multi-Tenancy Behavior

| TenancyStyle | Behavior | Mechanism |
|---|---|---|
| `Conjoined` | `WHERE TenantId = 'x'` auto-injected | Inherited from queryable's `ApplyTenantFilter()` |
| `DatabasePerTenant` | No tenant filter in WHERE | `ApplyTenantFilter()` already skips for this mode; DB-level isolation |
| `None` | No tenant filter | Normal behavior |

Types without a `TenantId` property get no tenant filter regardless of `TenancyStyle` (existing `HasTenantProperty()` check).

## Edge Cases

| Case | Behavior |
|------|----------|
| No WHERE clause | `UPDATE {table} SET ...` — updates ALL rows |
| No rows match | Returns 0 |
| SurrealDB error | Throws `SurrealDbException` (reuses existing error handling path) |
| Table doesn't exist | SurrealDB throws — consistent with other bulk operations |
| Empty update values | **Should throw** `ArgumentException` — no point running an UPDATE with no SET |
| Null update values | **Should throw** `ArgumentNullException` |
| Update values with null field | Emit `= NONE` in SurrealQL |

## Files to Create/Modify

| File | Change |
|------|--------|
| `src/Dali/Linq/SurrealQueryExtensions.cs` | Add `BulkUpdateAsync<T>` extension method |
| `tests/Dali.Tests/BulkUpdateTests.cs` | New file — unit + integration tests |

## Out of Scope (Future)

- Typed update expression: `.BulkUpdateAsync(x => new { x.Status = "Incoming" })` — would require expression tree parsing for member assignments
- Per-row value expressions: `.Set(x => x.Count, x => x.Count + 1)` — would require dual expression compilation (filter + value)
- `BulkUpdateAsync` on `IQueryable<T>` instead of `ISurrealDbQueryable<T>` — would lose tenant filter injection
