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

---

## Schema Migration / Drift Detection

### Problem

`EnsureDocumentSchemaAsync<T>()` currently creates the table via `DEFINE TABLE` only if it doesn't already exist. When the C# model (or source-gen output) adds/removes fields after the table was created, SaveChanges fails with `"Found field '...' but no such field exists"` — the SurrealDB table is never altered.

### What's needed

After `DEFINE TABLE`, iterate the existing table's fields via SurrealDB `INFO FOR TABLE`, diff them against the compiled schema from source-gen/SchemaManager, and emit:

- `DEFINE FIELD IF NOT EXISTS ...` for missing fields
- `REMOVE FIELD ...` for fields no longer present (with caution — may require an opt-in flag)

### Where

New `SchemaMigrator` class in `AeroDB.Sable/Schema/` — called from `DocumentStore.InitializeAsync` alongside `EnsureDocumentSchemaAsync`, or as an opt-in phase via `AeroDBOptions.AutoMigrateSchema`.

### Priority

**Medium** — Production readiness for iterative schema changes without database wipes.

---

## GenerateSchemaMigration (SurrealQL Export)

### Goal

Export all document schemas registered in `IDocumentStore` to `.surql` files for:

- CI/CD pipeline application
- External database provisioning (DB-as-code)
- Offline migration review

### What's needed

- `IDocumentStore.GenerateSchemaMigrationAsync(string outputDir)` — iterates all registered document types from `Schema.For<T>()` calls and source-gen metadata, emits `DEFINE TABLE` + `DEFINE FIELD` per type into `.surql` files
- Should skip fields that only exist as table defaults
- Output must be valid SurrealQL runnable via `surreal sql` CLI or REST API
- One `.surql` file per table (e.g. `docs_page.surql`, `tenant_model.surql`)

### Design options

- Extension method on `DocumentStore` / `IDocumentStore`
- Standalone `SchemaMigrationExporter` class consuming the store's compiled schema configuration
- Open question: single compound file vs per-table files — per-table is more CI-friendly for targeted migration

### Priority

**Medium** — Production readiness for database provisioning and migration pipelines.
