# Dali Client Testing Gaps

## What This Document Covers

The test suite has **62 purely structural tests** — tests that only verify method names, return types, parameter types, or interface inheritance via reflection. These tests verify what the **C# compiler already enforces** and provide no behavioral coverage.

This plan maps each structural test to a **real behavioral test** that exercises the Dali client through its LINQ/fluent API — never through raw SurrealQL unless absolutely necessary (e.g., `ExecuteSqlAsync`).

---

## 1. Graph Traversal (29 structural tests → replace all)

### Files
- `GraphTests.cs` — `GraphReflectionTests` class (29 tests)
- Delete the entire `GraphReflectionTests` class

### Real Tests Needed

| Test Area | What to Exercise | Count |
|-----------|-----------------|-------|
| **Forward traversal** | `session.Graph<Person>().Out<Company>("works_for").ToListAsync()` — verify correct `Company` records returned | 2 |
| **Backward traversal** | `session.Graph<Company>().In<Person>("works_for").ToListAsync()` | 2 |
| **Bidirectional** | `Both<Person>("knows")` — verify both directions | 2 |
| **Typed edge** | `Out<Person, KnowsEdge>()` where `KnowsEdge : EdgeRecord` — typed inference | 2 |
| **Wildcard** | `OutAny()`, `InAny()`, `AnyEdge()` — returns `GraphNode` | 3 |
| **Depth** | Fixed (`Depth(3)`), range (`Depth(1, 3)`), open (`Depth()`) — verify correct node count | 3 |
| **ShortestPath** | `ShortestPath("person:target")` — returns shortest path | 1 |
| **ReturnPath / CollectAll** | `ReturnPath().ToListAsync()` returns `GraphPath` list | 2 |
| **IncludeOrigin / IncludeIntermediate** | Nodes include origin/intermediate in result | 2 |
| **Graph Where filter** | `Where(p => p.Name == "Alice").Out<...>()` — filter on first step | 1 |
| **Terminal ops** | `FirstOrDefaultAsync`, `CountAsync`, `ToPathListAsync` on a graph query | 3 |
| **Edge data** | Verify edge `In`/`Out` properties are populated correctly | 2 |
| **Multi-hop** | `Out<T>(...).Out<T>(...)` — chaining multiple traversal steps | 2 |
| **Multiple edge types** | `Out<T>(new[] { "knows", "works_for" })` | 1 |
| **Total** | | **28** |

### Data seeding
```csharp
session.Store(new Person { Name = "Alice" });
session.Store(new Person { Name = "Bob" });
session.Store(new Company { Name = "Acme" });
await session.SaveChangesAsync();
session.Relate<KnowsEdge>(aliceId, bobId, new KnowsEdge { Since = 2020 });
session.Relate<WorksForEdge>(aliceId, acmeId, new WorksForEdge { Role = "Engineer" });
```

---

## 2. ISurrealDbQueryable Eager Loading (15 structural tests → replace all)

### Files
- `QueryFetchIncludeTests.cs` — `QueryFetchIncludeReflectionTests` class (12 tests) + `FilterIncludeIntegrationTests` (3 tests)
- Delete the reflection class, keep behavioral tests (already 85 exist)

### Real Tests Needed

| Test Area | What to Exercise | Count |
|-----------|-----------------|-------|
| **FETCH clause** | `.Include(i => i.AssigneeId).ToListAsync()` — verify referenced doc is populated inline | 2 |
| **IncludeBatch callback** | `.IncludeBatch(i => i.AssigneeId, user => names.Add(user.Name)).ToListAsync()` | 2 |
| **IncludeBatch dictionary** | `.IncludeBatch(i => i.AssigneeId, users).ToListAsync()` — verify dictionary populated | 2 |
| **IncludeReverse** | `.IncludeReverse(i => i.Comments, "issue_id").ToListAsync()` — child collection populated | 2 |
| **Include (inline subquery)** | `.Include(i => i.Team).ToListAsync()` — single subquery | 2 |
| **FilterInclude** | `.FilterInclude(i => i.Comments, c => c.IsApproved).ToListAsync()` — filtered children | 2 |
| **Chained includes** | Fetch + IncludeBatch + IncludeReverse on same query | 2 |
| **Null keys** | Document with null FK should not cause error | 1 |
| **Multiple includes** | Two separate IncludeBatch calls on one query | 1 |
| **Total** | | **14** |

---

## 3. Interface Member Presence (SchemaGaps — 13 structural tests → 0 needed)

### Files
- `SchemaGapsTests.cs` — 13 structural tests about member presence on interfaces

### Decision
**Delete all 13.** These test compiler-enforced contracts:
- `DeleteWhere<T>` exists on `IDocumentSession` → compiler enforces this
- `BulkInsertAsync` exists on `IDocumentSession` → compiler enforces this
- `ISubscriber<>` is generic → compiler enforces this

### Real Tests to Add Elsewhere
| Current Structural Test | Where It Already Has Behavioral Coverage |
|------------------------|------------------------------------------|
| `BulkInsert_PromotedToInterface` | `StoreOptionsAndBulkOperationsTests.cs` (16 tests) |
| `Advanced_HasDiagnostics` | `DaliAdvancedOperationsTests.cs` (8 tests), `QueryDiagnosticsTests.cs` (2 tests) |
| `Advanced_DeleteAllDocuments_ExistsOnInterface` | `DaliAdvancedOperationsTests.cs` |
| `Diagnostics_HasPreviewCommand` | `QueryDiagnosticsTests.cs` |
| `ICompiledQuery_On_Store_Exists` | `CompiledQueryTests.cs`, `CompiledQueryInterfaceTests.cs` |
| `ISubscriber_Interface_Exists` | Covered by existing behavioral projection tests |
| `Projection_Has_Name_Property` | Covered by existing behavioral projection tests |

---

## 4. WolverineOptions Extensions (6 structural tests → consolidate)

### Files
- `WolverineOptionsExtensionsTests.cs` — 6 tests checking IntegrateWithDali / PersistMessagesWithDali are public static extension methods

### Decision
**Delete 5 reflection tests. Keep 1 behavioral test via the actual DI registration:**

| Test | Action |
|------|--------|
| `IntegrateWithDali_IsPublicExtensionMethod` | Delete (compiler-enforced) |
| `PersistMessagesWithDali_IsPublicExtensionMethod` | Delete (compiler-enforced) |
| `IntegrateWithDali_FirstParameter_IsWolverineOptions` | Delete (compiler-enforced) |
| `PersistMessagesWithDali_FirstParameter_IsWolverineOptions` | Delete (compiler-enforced) |
| `WolverineOptionsDaliExtensions_IsStaticClass` | Delete (compiler-enforced) |
| `WolverineOptionsDaliExtensions_HasTwoPublicMethods` | Delete (compiler-enforced) |

Already covered by `DaliIntegrationRegistrationTests.cs` (full class of 14 tests that exercise the actual `DaliIntegration.Configure()` method).

---

## 5. DocumentSessionListener Hooks (1 structural test → behavioral)

### Files
- `DocumentSessionTransactionTests.cs` — `IDocumentSessionListener_HasNewTransactionHooks`

### Decision
Already have behavioral `LifecycleHooksTests.cs` (14 tests) covering all listener hooks. **Delete this test.**

---

## 6. IDaliOp / ISideEffect (1 structural test → behavioral)

### Files
- `DaliOpsTests.cs` — `IDaliOp_Implements_ISideEffect`

### Decision
**Replace with:**
- Create a side effect handler class that implements `IDaliOp`, register it, and verify it fires during projection. This is already partially covered in `SideEffectTests.cs` (5 tests). If that file covers this, **delete**.

---

## 7. EventTrigger RemoveTrigger (1 structural test → behavioral)

### Files
- `EventTriggerTests.cs` — `EventTriggerManager_RemoveTrigger_BuildsCorrectSurql`

### Decision
**Replace with behavioral test:** Actually call `EventTriggerManager.RemoveTriggerAsync(name, table, ct)` and verify the trigger is removed (e.g., list triggers before/after, or verify the remove SurrealQL through `IDiagnostics.PreviewCommandAsync`).

---

## 8. Thin Area Coverage

These areas have existing tests but are thin:

| Area | Existing Coverage | What's Missing |
|------|------------------|----------------|
| **CompactStream** | `EventReplayTests.cs` (23 tests) may cover it | Verify a multi-event stream is compacted into a single snapshot event |
| **ArchiveStream** | `EventReplayTests.cs` | Verify archived streams reject new appends; verify FetchStreamState.IsArchived |
| **WriteTombstone** | `EventReplayTests.cs` | Verify tombstone fills gap correctly in sequence numbers |
| **OverwriteEventAsync** | `EventReplayTests.cs` | Verify event data is replaced, metadata preserved |
| **DeleteSingleEventAsync** | `EventReplayTests.cs` | Verify event is soft-deleted (data cleared, metadata intact) |
| **StreamJson** | None | `session.StreamJson<T>(stream, sql, params)` — verify JSON written to stream |
| **WatchTableAsync** | None | Marten parity — verify subscription fires on document changes (requires WebSocket) |
| **QueueSqlCommand** | None | Verify SQL executes during SaveChangesAsync |
| **Store-level Bulk** | `StoreOptionsAndBulkOperationsTests.cs` | Missing: `BulkInsertDocumentsAsync(mixed)`, `BulkInsertEventsAsync` on `IDocumentStore` |
| **Daemon lifecycle** | `DaemonHealthCheckTests.cs` (9 tests) | Missing: daemon start/stop/restart, subscription lifecycle |

---

## Summary of Actions

| Action | Count | Details |
|--------|-------|---------|
| **Delete** | 19 | Tests that only verify compiler-enforced contracts (method names, return types, interface inheritance) |
| **Replace with behavioral** | 43 | Graph (28), ISurrealDbQueryable (14), IDaliOp (1) — structural → real behavioral via LINQ/fluent API |
| **Add thin-area coverage** | ~20 | CompactStream, ArchiveStream, WriteTombstone, OverwriteEvent, DeleteSingleEvent, StreamJson, WatchTable, QueueSqlCommand, Store-level Bulk, Daemon lifecycle |
| **Total** | ~82 | Tests to write or move |

### Priority Order

1. **P0 — Replace structural graph tests** (28 tests). Graph is the most feature-rich API with the least behavioral coverage per method.
2. **P0 — Replace structural eager-loading tests** (14 tests). Eager loading (FETCH/Include) is heavily used and has 85 behavioral tests already, but these 14 structural ones should become behavioral edge cases.
3. **P1 — Event lifecycle thin areas** (~10 tests). CompactStream, ArchiveStream, tombstone, overwrite, and delete-single-event are critical for event-sourced systems.
4. **P2 — StreamJson, WatchTable, QueueSqlCommand, Daemon lifecycle** (~10 tests). Marten parity features.
5. **P3 — Remove compiler-enforced structural tests** (19 deletions). No behavioral replacement needed.

### Rules for Writing These Tests

- Use `await using var store = await TestHarness.CreateStoreAsync()` — standard test harness pattern
- Use `TestHarness.CreateStoreAsync(o => /* configure */)` for non-default config
- Prefer LINQ/fluent API over raw queries
- Use SurrealDB Embedded InMemory for speed
- Seed via `session.Store()` + `session.SaveChangesAsync()`
- Assert via `Shouldly` (`ShouldBe`, `ShouldContain`, `ShouldNotBeNull`, etc.)
- Use `[Test]` from TUnit
- Group by feature area in clear file-per-feature structure
