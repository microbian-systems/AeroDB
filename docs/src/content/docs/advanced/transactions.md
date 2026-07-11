---
title: Transactions
description: Transaction support in AeroDB — unit of work, explicit transactions, optimistic concurrency, and batch operations
---

AeroDB provides comprehensive transaction support built on SurrealDB's MVCC (Multi-Version Concurrency Control) engine. Transactions ensure atomicity, consistency, and isolation for your document operations.

## Unit of Work Pattern

The session acts as a unit of work. All operations you perform — `Store`, `Delete`, `Insert`, event appends — are queued in memory and flushed atomically when you call `SaveChangesAsync()`.

```csharp
// All queued operations are committed atomically
session.Store(new User { Name = "Alice" });
session.Store(new User { Name = "Bob" });
session.Delete<User>("user:3");

// Single SurrealDB transaction
await session.SaveChangesAsync();
```

If any operation within `SaveChangesAsync` fails, the entire transaction is rolled back and no changes are persisted. Exceptions (except `ConcurrencyException`) are wrapped in an `InvalidOperationException`.

```csharp
try
{
    await session.SaveChangesAsync();
}
catch (InvalidOperationException ex)
{
    // Transaction was rolled back — all changes discarded
    _logger.LogError(ex, "Save failed");
}
```

## Explicit Transactions

When you need fine-grained control over the transaction lifecycle — such as coordinating multiple `SaveChangesAsync` calls — use explicit transactions.

```csharp
// Begin an explicit transaction
await using var tx = await session.BeginTransactionAsync();

session.Store(new Order { Total = 99.95m });
await session.SaveChangesAsync(); // Runs inside the transaction (no auto-commit)

session.Store(new Invoice { OrderId = orderId });
await session.SaveChangesAsync(); // Same transaction

// Commit both SaveChangesAsync calls atomically
await tx.CommitAsync();
```

### API Surface

| Method | Description |
|--------|-------------|
| `BeginTransactionAsync()` / `BeginTransaction()` | Starts a new SurrealDB transaction. Throws if one is already active. |
| `CommitTransactionAsync()` | Commits the current explicit transaction. |
| `RollbackTransactionAsync()` | Rolls back the current explicit transaction. |
| `IAeroDBTransaction` | Disposable wrapper; `DisposeAsync()` rolls back if not committed. |

```csharp
// Also available directly on the session
await session.CommitTransactionAsync();
// or
await session.RollbackTransactionAsync();
```

### Nested Transactions

SurrealDB does not support nested transactions. If you call `BeginTransactionAsync()` while a transaction is already active, AeroDB throws an `InvalidOperationException`:

```csharp
var tx1 = await session.BeginTransactionAsync();
var tx2 = await session.BeginTransactionAsync(); // 💥 InvalidOperationException
```

Always complete or roll back one transaction before starting another.

### Transaction Disposal

The `IAeroDBTransaction` returned by `BeginTransactionAsync()` implements `IAsyncDisposable`. If you do not call `CommitAsync()` before disposal, the transaction is automatically rolled back:

```csharp
// Rolled back automatically if CommitAsync is not called
await using var tx = await session.BeginTransactionAsync();
session.Store(new User { Name = "Charlie" });
await session.SaveChangesAsync();
// tx.DisposeAsync() rolls back — no changes persisted
```

## Transaction Scoping

AeroDB supports three levels of transaction scoping:

| Scope | Trigger | Commit Behavior |
|-------|---------|-----------------|
| **Implicit** | No explicit transaction | `SaveChangesAsync` auto-creates, commits, and disposes a transaction |
| **Explicit (session)** | `BeginTransactionAsync()` | Caller must call `CommitAsync()` or `RollbackAsync()` |
| **Coordinated (EF Core)** | `AeroDBEfCoreTransactionManager` | `SaveChangesAsync` + EF Core `SaveChangesAsync` coordinated atomically |

### Implicit (Default)

Every call to `SaveChangesAsync()` without an active explicit transaction automatically wraps all pending operations in a SurrealDB transaction. The transaction is committed on success and rolled back on exception.

### Explicit via IAeroDBTransaction

```csharp
await using var tx = await session.BeginTransactionAsync();
try
{
    // Multiple batches of work
    session.Store(batch1);
    await session.SaveChangesAsync();

    session.Store(batch2);
    await session.SaveChangesAsync();

    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();
    throw;
}
```

### Coordinated with EF Core

The `AeroDB.EntityFrameworkCore` bridge provides `AeroDBEfCoreTransactionManager<TDbContext>` to coordinate transactions between AeroDB (SurrealDB) and Entity Framework Core:

```csharp
// Both AeroDB and EF Core changes commit atomically
await using var tx = await transactionManager.BeginTransactionAsync();

session.Store(new AuditLog { Action = "UserCreated" });
dbContext.Users.Add(new User { Name = "Alice" });

await session.SaveChangesAsync();     // Queued in AeroDB
await dbContext.SaveChangesAsync();   // Queued in EF Core

await tx.CommitAsync();              // Both commit atomically
```

## Optimistic Concurrency

AeroDB supports optimistic concurrency control to prevent lost updates. When enabled, each document carries a version number that is checked before persisting modifications.

### Enabling Concurrency

Enable globally on `StoreOptions`:

```csharp
opts.UseOptimisticConcurrency = true;          // Enables for all documents
opts.AllDocumentsEnforceOptimisticConcurrency(); // Same, via policy
```

Or per document type via policy:

```csharp
opts.Policies.ForDocumentsOfType<User>()
    .UseOptimisticConcurrency = true;
```

### Version Field

Mark a document with a version field using either the `IVersioned` interface or the `[Version]` attribute:

```csharp
// Option 1: IVersioned interface
public class Product : IVersioned
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public long Version { get; set; }
}

// Option 2: [Version] attribute (takes precedence)
public class Customer
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = string.Empty;

    [Version]
    public long ConcurrencyStamp { get; set; }
}
```

### Concurrency Conflict Handling

When a version mismatch is detected during `SaveChangesAsync`, a `ConcurrencyException` is thrown:

```csharp
try
{
    await session.SaveChangesAsync();
}
catch (ConcurrencyException ex)
{
    // ex.DocumentType   → typeof(Product)
    // ex.DocumentId     → "product:42"
    // ex.ExpectedVersion → 3
    // ex.ActualVersion   → 5

    _logger.LogWarning(
        "Concurrency conflict on {Type} (id={Id}): expected {Expected}, actual {Actual}",
        ex.DocumentType.Name, ex.DocumentId, ex.ExpectedVersion, ex.ActualVersion);

    // Strategy: reload, reapply changes, retry
    var latest = await session.LoadAsync<Product>("product:42");
    latest.Price = newPrice;
    latest.Version = ((IVersioned)latest).Version;
    session.Store(latest);
    await session.SaveChangesAsync();
}
```

### Expected Version / Revision

For advanced scenarios, you can gate a specific entity update on an expected version or revision:

```csharp
// Fail if version doesn't match
session.UpdateExpectedVersion(entity, expectedVersion: 3);
await session.SaveChangesAsync();

// Skip update silently if revision doesn't match
session.TryUpdateRevision(entity, revision: 2);
await session.SaveChangesAsync();
```

## Batch Operations

Grouping multiple operations in a single `SaveChangesAsync()` reduces round-trips and ensures atomicity.

### Batched Inserts

```csharp
// Queue many documents and commit at once
for (int i = 0; i < 1000; i++)
{
    session.Store(new Product
    {
        Name = $"Product {i}",
        Price = Random.Shared.Next(1, 1000)
    });
}

// Single transaction — all 1000 documents committed atomically
await session.SaveChangesAsync();
```

### BulkInsert

For very large datasets, `BulkInsertAsync` uses SurrealDB's `INSERT INTO ... [...]` array syntax with configurable batch sizing:

```csharp
// More efficient than N individual Store calls
var products = Enumerable.Range(0, 10_000)
    .Select(i => new Product { Name = $"Product {i}" })
    .ToList();

// Default batch size: 100 documents per SurrealDB INSERT
await session.BulkInsertAsync(products, batchSize: 500, ct);
```

### Batch Size

Control the maximum number of operations per batch with `UpdateBatchSize`:

```csharp
opts.UpdateBatchSize = 1000; // Default is 500
```

### Atomic Commit Guarantee

All operations queued in a single `SaveChangesAsync()` are committed atomically within a single SurrealDB transaction. Partial commits are not possible — either all operations succeed or the entire batch is rolled back.

## Distributed Transactions

### MVCC Isolation

SurrealDB uses Multi-Version Concurrency Control (MVCC) to provide snapshot isolation. Each transaction sees a consistent snapshot of the database as of the time the transaction began. Concurrent writers are detected via version checks, and conflicts cause one writer to retry.

### Transaction Durability

SurrealDB transactions are durable once committed. The engine uses a write-ahead log (WAL) to ensure committed changes survive process restarts. AeroDB surfaces the SurrealDB transaction lifecycle directly — when `SaveChangesAsync` returns successfully, the data is persisted.

### Cross-Database Transactions

SurrealDB does not support transactions that span multiple databases. AeroDB validates this at the start of every `SaveChangesAsync` and throws an `InvalidOperationException` if the unit of work targets more than one database:

```csharp
// 💥 InvalidOperationException: "Cross-database transactions are not supported."
// This fails if products and orders reside in different SurrealDB databases.
```

### Two-Phase Commit (2PC)

SurrealDB does not implement distributed two-phase commit. For multi-resource transactions (e.g., SurrealDB + PostgreSQL + message queue), use the Sagas pattern or an orchestrator like Wolverine with the `AeroDB.WolverineFx` integration.

## Summary

| Feature | Support |
|---------|---------|
| Implicit transaction per `SaveChangesAsync()` | ✅ Yes |
| Explicit `BeginTransactionAsync` / `CommitAsync` | ✅ Yes |
| Automatic rollback on exception | ✅ Yes |
| Rollback on `IAsyncDisposable` disposal | ✅ Yes |
| Nested transactions | ❌ Not supported (throws) |
| Optimistic concurrency (`IVersioned` / `[Version]`) | ✅ Yes |
| Expected version / revision gating | ✅ Yes |
| `ConcurrencyException` with conflict details | ✅ Yes |
| Cross-database transactions | ❌ Not supported (throws) |
| Distributed 2PC | ❌ Use Sagas / Wolverine |
| EF Core coordinated transaction | ✅ Via `AeroDB.EntityFrameworkCore` |
