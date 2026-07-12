# Future Features

## Marten-style Fluent Batch Query API (v2 polish)

### What already exists

`IBatchedQuery` / `BatchedQuery` in `Batching/` — multi-statement batching, compiled query support, event batching.  
But the surface API uses `out Task<T>` parameters instead of returning tasks directly, and has no chainable `.Where()` support:

```csharp
// Current (works, but clunky)
batch.Load<User>("1", out var userTask);
batch.Query<User>(out var postsTask);
await batch.Execute();
var user = await userTask; // Task<User?>
```

### Target syntax (needs implementing)

```csharp
var batch = session.CreateBatchQuery();

var userTask         = batch.Load<User>(1);                           // Task<User?>
var postsTask        = batch.Query<Post>().Where(p => p.UserId == 1).ToListAsync();  // Task<List<Post>>
var achievementsTask = batch.Query<Achievement>().Where(a => a.UserId == 1).ToListAsync();

await batch.Execute();

var user         = await userTask;
var posts        = await postsTask;
var achievements = await achievementsTask;
```

### What's missing

1. `IBatchedQuery.Load<T>(object id)` — returns `Task<T?>` (new overload, existing uses `out Task<T?>`)
2. `IBatchedQuery.Query<T>()` — returns `IBatchedQueryable<T>`
3. `IBatchedQueryable<T>` — chainable LINQ builder with:
   - `.Where(Expression<Func<T, bool>>)` → `IBatchedQueryable<T>`
   - `.OrderBy<TKey>(...)` / `.OrderByDescending<TKey>(...)`
   - `.Take(int)` / `.Skip(int)`
   - `.ToListAsync()` → `Task<List<T>>`
   - `.FirstOrDefaultAsync()` → `Task<T?>`
   - `.AnyAsync()` → `Task<bool>`
   - `.CountAsync()` → `Task<int>`

### Implementation notes

- Reuse existing `SurrealExpressionVisitor` to compile LINQ expressions to SurrealQL WHERE clauses
- Reuse existing `BatchedQuery.InlineParameters` for parameter inlining
- `BatchedQueryable<T>` is a lightweight builder (not a full `IQueryProvider` like `ISurrealDbQueryable<T>`)
- Keep existing `out Task` overloads for backwards compat, or deprecate them

### Design decision: eager vs lazy Execute

- **Current design** (Marten-compatible): call `batch.Execute()` explicitly, then await tasks
- **Lazy alternative**: have awaiting any task auto-trigger `Execute()` so `Task.WhenAll()` works naturally
