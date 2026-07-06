---
title: Documents
description: Document storage in AeroDB
---

AeroDB maps .NET POCOs to SurrealDB records. Each document is stored as a row in a SurrealDB table, identified by a `table:id` record ID.

## Identity Patterns

Documents can use several identity strategies:

- **Snowflake IDs** (`long`) — Generated via `SnowflakeGenerator.NewId()`, which wraps the FlakeId library for distributed, time-sorted IDs.
- **String IDs** — Custom or domain-specific identifiers.
- **Guid IDs** — Globally unique identifiers.
- **SurrealDB auto-assigned** — If no ID is set before `SaveChangesAsync`, SurrealDB generates a unique record ID.

Convenience base classes are provided:

```csharp
public class User : EntitySnowlake { }     // long Id, Snowflake-generated
public class Tenant : EntityString { }     // string Id, Snowflake-generated as string
public class Session : EntityGuid { }      // Guid Id
```

The `IEntity<TId>` interface and `Entity<TId>` base class define the `Id` property:

```csharp
public interface IEntity<TId> where TId : notnull, IEquatable<TId>, IComparable<TId>
{
    TId Id { get; set; }
}
```

## Record IDs in SurrealDB

SurrealDB identifies records with the format `table:id` (e.g., `user:185473209184321536`). AeroDB translates between .NET identity properties and SurrealDB record IDs transparently:

- When storing an entity with `Id = 42`, AeroDB creates/upserts `user:42`
- When loading, the record ID `user:42` is parsed and the numeric portion is written to the `Id` property

## Document Lifecycle

Documents follow a clear lifecycle through `IDocumentSession`:

```
Store() / Insert() / Update()   →   SaveChangesAsync()   →   Committed
                                                                          
LoadAsync() / Query()           ←   (database)
                                                                          
Delete() / HardDelete()         →   SaveChangesAsync()   →   Removed
```

```csharp
// Store
await using var session = await store.LightweightSessionAsync();
session.Store(new User { Id = SnowflakeGenerator.NewId(), Name = "Alice" });
session.SaveChangesAsync();

// Load
var user = await session.LoadAsync<User>(id);

// Query
var admins = await session.Query<User>()
    .Where(u => u.Role == "admin")
    .ToListAsync();

// Delete
session.Delete(user);
await session.SaveChangesAsync();
```

## Optimistic Concurrency

When `UseOptimisticConcurrency` is enabled, AeroDB tracks entity versions. Before committing a modified document, it checks that the stored version matches the version captured at load time. If another process changed the document in the meantime, a `ConcurrencyException` is thrown.

Version fields can be an `IVersioned` interface implementation, a property decorated with `[Version]`, or a named property resolved by the source generator.

```csharp
store.Options.UseOptimisticConcurrency = true;

// Or per type:
store.Options.Policies.ForDocumentsOfType<User>()
    .UseOptimisticConcurrency = true;
```
