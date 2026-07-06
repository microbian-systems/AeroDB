---
title: Relational
description: Document relationships and CRUD in AeroDB
---

AeroDB models relationships between documents through foreign key references and eager loading via `Include` / `ThenInclude`. Relationship edges in SurrealDB are represented as record ID fields — a `customer` field storing `customer:abc123` links an order to its customer.

## Configuring Relationships

Foreign key metadata is declared with `ForeignKey()` for documentation and tooling purposes. In SurrealDB, graph edges replace traditional FK cascades, so this metadata is informational.

```csharp
options.Schema.For<Order>()
    .ForeignKey<Customer>(o => o.CustomerId);
```

### Nested Navigation Properties

Declare navigation properties on your entities to enable `Include`:

```csharp
public class Order : EntitySnowflake
{
    public long CustomerId { get; set; }
    public string Status { get; set; }
    public decimal Total { get; set; }

    // Navigation property (not persisted directly)
    public Customer Customer { get; set; }
}

public class Customer : EntitySnowflake
{
    public string Name { get; set; }
    public string Email { get; set; }
}

public class LineItem : EntitySnowflake
{
    public long OrderId { get; set; }
    public long ProductId { get; set; }
    public int Quantity { get; set; }
    public Product Product { get; set; }
}
```

## Loading Related Documents

### Include / ThenInclude

Eagerly load related entities in a single query:

```csharp
var orders = await session.Query<Order>()
    .Include(o => o.Customer)
    .Include(o => o.LineItems)
    .ThenInclude(li => li.Product)
    .Where(o => o.Status == "shipped" && o.Total > 50)
    .OrderByDescending(o => o.Total)
    .ToListAsync();
// Each order has Customer and LineItems populated;
// each LineItem has Product populated.
```

### Deep Nested Includes

Multiple levels of `ThenInclude`:

```csharp
var result = await session.Query<Company>()
    .Include(c => c.Departments)
    .ThenInclude(d => d.Manager)
    .ThenInclude(m => m.ContactInfo)
    .Include(c => c.Address)
    .ToListAsync();
```

## CRUD Operations

AeroDB's CRUD operations follow a unit-of-work pattern through `IDocumentSession`.

### Store (Create / Upsert)

Insert a new document. AeroDB assigns a SurrealDB record ID automatically:

```csharp
await using var session = store.LightweightSessionAsync();

var user = new User
{
    Name = "Alice",
    Email = "alice@example.com",
    Role = "admin"
};
session.Store(user);
await session.SaveChangesAsync();
```

Use `InsertAsync` when you need an immediate flush outside the unit of work:

```csharp
await session.InsertAsync(user);
```

Store multiple documents in batch:

```csharp
var users = new List<User>
{
    new User { Name = "Bob", Email = "bob@example.com" },
    new User { Name = "Carol", Email = "carol@example.com" }
};
session.Store(users);      // Enqueue all
await session.SaveChangesAsync(); // Single round-trip
```

### Load (Read)

Load a single document by record ID:

```csharp
var user = await session.LoadAsync<User>("user:185473209184321536");
```

Load multiple documents by ID:

```csharp
var ids = new[] { "user:1", "user:2", "user:3" };
var users = await session.LoadManyAsync<User>(ids);
```

### Update

Load, modify, and store:

```csharp
var user = await session.LoadAsync<User>("user:123abc");
user.Name = "Alice Smith";
user.Email = "alice@newdomain.com";
session.Store(user);
await session.SaveChangesAsync();
```

For partial updates without loading the full document, use `PatchAsync`:

```csharp
await session.PatchAsync<User>("user:123abc", patches =>
    patches.Replace(u => u.Email, "alice@newdomain.com")
           .Add(u => u.Tags, "premium")
           .Remove(u => u.Tags, "trial"));
```

### Delete

```csharp
// Soft delete (if ISoftDeleted is implemented)
session.Delete(user);
await session.SaveChangesAsync();

// Hard delete by ID
await session.DeleteAsync<User>("user:123abc");

// Delete with predicate
await session.DeleteWhereAsync<User>(u => u.Status == "inactive");

// Batch delete
var ids = new[] { "user:1", "user:2", "user:3" };
await session.DeleteAsync<User>(ids);
```

## SaveChangesAsync and Unit of Work

All `Store`, `Insert`, and `Delete` calls are tracked in-memory until `SaveChangesAsync` flushes them atomically:

```csharp
session.Store(user1);
session.Store(user2);
session.Delete(user3);
session.Store(batchOfUsers);     // IEnumerable overload
await session.SaveChangesAsync(); // Atomic commit
```

The unit-of-work pattern ensures transactional consistency. If any operation fails, the entire batch is rolled back.

### Batch Size Control

Configure the maximum batch size in `StoreOptions`:

```csharp
options.UpdateBatchSize = 500; // Default
```

When the pending change count exceeds `UpdateBatchSize`, changes are flushed in multiple batches within the same transaction.

### Listeners and Hooks

Register document session listeners that fire during `SaveChangesAsync`:

```csharp
options.Listeners.Add(new AuditTrailListener());
options.Listeners.Add(new SoftDeleteListener());
```

## Optimistic Concurrency

When enabled, AeroDB tracks entity versions and detects concurrent modifications:

```csharp
options.UseOptimisticConcurrency = true;

// Or per type:
options.Policies.ForDocumentsOfType<User>()
    .UseOptimisticConcurrency = true;
```

On conflict, `SaveChangesAsync` throws `ConcurrencyException`:

```csharp
try
{
    session.Store(user);
    await session.SaveChangesAsync();
}
catch (ConcurrencyException ex)
{
    var latest = await session.LoadAsync<User>(user.Id);
    // Merge changes and retry
    user.RowVersion = latest.RowVersion;
    session.Store(user);
    await session.SaveChangesAsync();
}
```

## SurrealDB Relationship Paradigm

Full SurrealDB relationship support — including `RELATE` statements, graph edges, and `->edge<-` traversal — is managed through the graph API. See the [Graphs](/docs/graphs) documentation for:

- Defining edge tables with `Schema.For<T>().Edge()`
- Creating relationships with `session.RelateAsync()`
- Graph traversal with `session.Graph<T>()`

> **Note:** Full SurrealDB relationship paradigm (RELATE, graph edges) is covered in the Graphs documentation. The relational API described here uses foreign-key-style references with eager loading via Include/ThenInclude. True graph relationships (edges as separate records with IN/OUT) are available through the graph API.
