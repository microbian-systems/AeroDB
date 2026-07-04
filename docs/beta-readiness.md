# Beta Readiness Assessment

Date: 2026-07-02
Test suite: 1810 total / 0 failed / 1810 passed / 0 skipped

---

## Critical Issues (Fix Before Beta)

### C1-C3: Sync-over-Async Deadlock Risk

`.GetAwaiter().GetResult()` used in three places — blocks thread-pool threads and deadlocks under ASP.NET `SynchronizationContext`.

| File | Line | Method | Call |
|------|------|--------|------|
| `src/Dali/Configuration/DaliServiceCollectionExtensions.cs` | 30, 70 | DI registration | `store.InitializeAsync().GetAwaiter().GetResult()` |
| `src/Dali/DocumentStore.cs` | 574-575 | `Graph<T>()` | `Client.CreateSession(DefaultCt).GetAwaiter().GetResult()` |
| `src/Dali/DocumentSession.cs` | 154 | `BeginTransaction()` | `Session.BeginTransaction(DefaultCt).GetAwaiter().GetResult()` |

### C4: Blanket Exception Swallowing

`catch { return null; }` in `InternalSessionBase.cs:320` (and similar patterns in `EventStore.cs:45`, `InternalSessionBase.cs:890,915`) hides all exceptions — connection failures, auth errors, serialization failures — behind "document not found". Users silently lose data.

### C5: Non-Idempotent Initialization

`DocumentStore.cs:54` sets `_initialized = 1` via `Interlocked.Exchange` **before** initialization completes. If `InitializeAsync()` fails mid-way (client created, schema partially set up), the store is permanently corrupted — no retry possible without restart.

### I10: Event Store TOCTOU Race

`EventStore.cs:137-145` checks version via `GetNextVersion()` then appends via `CREATE` in separate round trips — **no SurrealDB transaction**. Concurrent appends corrupt event ordering.

---

## Important Issues (Should Fix Before Beta)

### I1: `Delete<T>(string id)` Requires Parameterless Ctor

`DocumentSession.cs:1829-1851` uses `Activator.CreateInstance<T>()` to create a stub entity. The constraint is only `class` — `MissingMethodException` at runtime for types without parameterless ctor.

### I2: Reflection-Intensive Hot Paths

`MakeGenericMethod` + `MethodInfo.Invoke` on hot paths (SaveChangesAsync, UpsertRecordAsync, CheckConcurrencyAsync, FetchVersionAsync, CreateEntityAsync). ~10-20x slower than compiled delegates.

### I3: `BulkDeleteAsync` Load-then-Delete

`src/Dali/Storage/BulkOperations.cs:56-64` loads every entity individually before deleting — O(n) extra SELECTs per delete.

### I4: `CleanDeletedDocumentsAsync` Wrong Counting

`DocumentStore.cs:656-658` counts 1 per table mapping instead of actual deleted records.

### I5: SQL Injection Risk in `DeleteWhere`

`DocumentSession.cs:399-416` builds SurrealQL via string concatenation. `FormatWhereValue` only escapes `'`, not backslashes/newlines or other special characters. Fallback `"true"` for complex predicates deletes all records.

### I6: `SearchAsync` Injects `_score` into Entities

`InternalSessionBase.cs:800` adds `search::score(0) AS _score` to SurrealQL — deserialized into entity. Non-`_score` types silently drop it or throw depending on `UnmappedMemberHandling`.

### I7: `StreamAsync<T1,T2>` Misleading Name

`InternalSessionBase.cs:645-671` executes two independent `RawQueryAsync` calls, not a true stream. 2 round trips, not 1.

### I8: Daemon Uses `CancellationToken.None`

`InternalSessionBase.cs:872,897` — daemon high-water-mark and max-sequence queries don't propagate cancellation token. Can't cancel polling loop.

### I9: `DatabasePerTenantSelector` Caches Failed Tasks

`DatabasePerTenantSelector.cs:36-37` — if `CreateClientAsync` fails, the failed `Task` is cached permanently in `ConcurrentDictionary`. No retry possible without restart.

### M1: Missing `ConfigureAwait(false)` in Several Call Sites

`DocumentSession.cs:1446,1464`, `QuerySession.cs`, `EventStore.cs:129` — library async calls without `ConfigureAwait(false)` risk context capture.

### M2: `SessionOptions.Timeout` Never Used

Property exists but never wired to SurrealDB session/client configuration.

### M3: `StoreOptions.ConfigureDali<T>` Is No-Op

`DaliServiceCollectionExtensions.cs:89-93` accepts `configure` delegate but never stores or executes it.

### M4: `DocumentStore.Daemon` Public Setter

`DocumentStore.cs:39` — external code can replace daemon reference, breaking lifecycle.

### M5: `FormatWhereValue` String Escaping Incomplete

Only escapes `'`. Backslashes, newlines, other SurrealQL special chars unescaped.

### M6: Reflection-Heavy `StoreObjects`/`DeleteObjects`/`InsertObjects`

Static `ConcurrentDictionary` cache without eviction — memory leak risk with dynamic types.

### M7: `_disposed` Flag Without Thread Safety

Read/written without synchronization in `DocumentStore.cs:18`, `DatabasePerTenantSelector.cs:19`, `InternalSessionBase.cs:28`.

### M8: `CleanSoftDeletedDocumentsAsync` PascalCase Field Names

Hardcodes `Deleted`, `DeletedAt` — inconsistent with CBOR field naming conventions.

---

## Raw Query Audit

### Tests Using Raw SurrealQL Where Fluent API Exists

| File | Raw Query | Fluent Replacement |
|------|-----------|--------------------|
| `tests/Dali.Tests/GraphTests.cs:508,522` | `SELECT ->knows->person.* FROM person` | `session.Graph<Person>().Out<Person>("knows")` |
| `tests/Dali.Tests/IntegrationTests.cs:55` | `SELECT *, ->customer->customer.* FETCH customer` | `.Include(o => o.Customer)` |
| `tests/Dali.Tests/RawQueryTests.cs` | `SELECT * FROM person WHERE Age > 25` | `.Where(p => p.Age > 25)` |
| `tests/Dali.Tests/ConcurrencyTests.cs` | `CREATE person:{id} CONTENT {...}` | `session.Store()` (gap: no explicit version on first write) |
| `tests/Dali.Tests/QuerySessionParityTests.cs:25` | `CREATE person:{id} CONTENT {...}` | `session.Store()` |
| `tests/Dali.Tests/LoadManyTests.cs` | `CREATE person:{id} CONTENT {...}` | `session.Store()` |
| `tests/Dali.Tests/IntegrationTests.cs:176` | `SELECT * FROM product WHERE Name = 'Test Product'` | `.Where(p => p.Name == "Test Product")` |
| `tests/Dali.Tests/AdvancedSqlTupleQueryTests.cs:168` | `CREATE person:... CONTENT {...}` | `session.Store()` |
| `tests/Dali.Tests/AdvancedSdkTests.cs:83` | `SELECT * FROM person WHERE name = 'AdvTest'` | `.Where(p => p.Name == "AdvTest")` |

### Tests Intentional (Testing Raw Query API)

`RawQueryTests.cs`, `AdvancedSqlTupleQueryTests.cs`, `ProjectionRebuildDiagnosticTests.cs`, `BatchedQueryTests.cs:560`, `LinqQuerySyntaxTests.cs:668` — these purposefully test the raw query pathway.

---

## Key Gaps (No Fluent Alternative Exists, By Design)

- **DDL operations**: `DEFINE TABLE/FIELD/INDEX/EVENT/FUNCTION`, `REMOVE TABLE/EVENT/FUNCTION`, `ALTER EVENT/TABLE` — `SchemaManager`/`FunctionManager`/`EventTriggerManager` are procedural wrappers, not fluent
- **Admin ops**: `DeleteAllDocumentsAsync`, `DeleteAllEventDataAsync`, `CompletelyRemoveAsync`, `CleanSoftDeletedDocumentsAsync`
- **Event overrides**: `OverwriteEventAsync`, `DeleteSingleEventAsync`
- **Diagnostics**: `ExplainPlanAsync`
- **Tuple/streaming**: `StreamAsync<T1,T2>`, `DaliAdvancedSql.QueryAsync<T1,T2>`
- **Soft-delete undo**: `UndoDeleteWhere`
- **Event live watch**: `WatchStreamAsync` (raw `LIVE SELECT FROM mt_events`)

---

## Overall Assessment

**Not ready for beta.** The sync-over-async patterns (C1-C3) and exception swallowing (C4) are production-showstoppers. The event store race condition (I10) causes data corruption under concurrent writes.

Fix the critical items first, then address important items before wider testing.
