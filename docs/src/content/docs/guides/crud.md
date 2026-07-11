---
title: CRUD Operations
description: Create, read, update, and delete documents
---

AeroDB provides a full set of CRUD operations through the `ISurrealDbSession` interface. All operations participate in a unit-of-work pattern with `SaveChangesAsync`.

## Creating Documents

Use `Store` to insert a new document. AeroDB assigns a SurrealDB record ID automatically.

```csharp
var user = new User { Name = "Alice", Email = "alice@example.com" };
session.Store(user);
await session.SaveChangesAsync();
```

Use `Insert` when you need an immediate flush:

```csharp
var user = new User { Name = "Bob", Email = "bob@example.com" };
await session.InsertAsync(user);
```

## Reading Documents

Load by record ID:

```csharp
var user = await session.LoadAsync<User>("user:123abc");
```

Query with LINQ:

```csharp
var adults = await session.Query<User>()
    .Where(u => u.Age >= 18)
    .OrderBy(u => u.Name)
    .ToListAsync();
```

## Updating Documents

Store an existing document (matched by record ID) to update it:

```csharp
var user = await session.LoadAsync<User>("user:123abc");
user.Name = "Alice Smith";
session.Store(user);
await session.SaveChangesAsync();
```

Use patch operations for partial updates:

```csharp
await session.PatchAsync<User>("user:123abc", patches =>
    patches.Replace(u => u.Email, "alice@newdomain.com")
           .Add(u => u.Tags, "premium"));
```

## Deleting Documents

```csharp
// Delete by ID
await session.DeleteAsync<User>("user:123abc");

// Delete with predicate
await session.DeleteWhereAsync<User>(u => u.Status == "inactive");
```

## Batch Operations

```csharp
var users = new List<User>
{
    new User { Name = "Charlie" },
    new User { Name = "Diana" }
};
session.Store(users); // enqueue all
await session.SaveChangesAsync(); // single round-trip

// Batch delete
var ids = new[] { "user:1", "user:2", "user:3" };
await session.DeleteAsync<User>(ids);
```

## SaveChangesAsync and Unit of Work

All `Store` calls are tracked in-memory until `SaveChangesAsync` flushes them in a single transaction:

```csharp
session.Store(user1);
session.Store(user2);
session.Delete(user3);
await session.SaveChangesAsync(); // atomic commit
```

## Optimistic Concurrency

AeroDB uses SurrealDB's record versioning for optimistic concurrency. On conflict, a `ConcurrencyException` is thrown:

```csharp
try
{
    session.Store(user);
    await session.SaveChangesAsync();
}
catch (ConcurrencyException ex)
{
    // Reload and retry
    var latest = await session.LoadAsync<User>(user.Id);
    // Merge changes and retry
}
```

Use the `version` field or `WHERE` clause predicates to guard updates in raw SurrealQL when you need fine-grained control.
