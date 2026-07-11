---
title: Entity Framework Core
description: EF Core bridge for coordinated transactions
---

# Entity Framework Core

AeroDB can be used alongside Entity Framework Core in the same application, sharing transactions and enabling hybrid workloads where some operations use the SurrealDB document API and others use relational EF Core providers.

## When to Use EF Core Alongside AeroDB

- **Legacy migration** — you have an existing EF Core codebase and are incrementally adopting AeroDB for new features.
- **Reporting queries** — EF Core with a relational provider (PostgreSQL, SQL Server) is better suited for complex SQL joins and aggregation.
- **Third-party integrations** — libraries that expect EF Core (e.g., Identity UI, Admin panels) can continue to use EF Core while AeroDB handles primary domain storage.

## AeroDB.EntityFrameworkCore Package

```shell
dotnet add package AeroDB.EntityFrameworkCore
```

The bridge package provides:

- `AeroDbTransaction` — shares ambient transactions with EF Core `DbContext`
- `IAeroDbExecutionStrategy` — coordinates retry logic across both stores
- `SerializationBridge` — keeps SurrealDB JSON serialization in sync with EF Core value converters

## Sharing Transactions

Use `AeroDbTransactionScope` to coordinate writes across AeroDB and EF Core within a single database transaction:

```csharp
public class OrderService
{
    private readonly AeroDbSession _session;
    private readonly OrderDbContext _dbContext;

    public async Task CreateOrderAsync(Order order)
    {
        // Both operations commit or roll back together
        await using var tx = await _session.StartTransactionAsync();
        await using var efTx = await _dbContext.Database.BeginTransactionAsync();

        // AeroDB document operation
        await _session.Create("order", order);

        // EF Core relational operation
        _dbContext.AuditLogs.Add(new AuditLog
        {
            Action = "OrderCreated",
            OrderId = order.Id
        });
        await _dbContext.SaveChangesAsync();

        // Commit both
        await efTx.CommitAsync();
        await tx.CommitAsync();
    }
}
```

> **Note:** Distributed transactions are not supported. The shared transaction works when both stores point to the same underlying database server (e.g., PostgreSQL via SurrealDB's PG storage engine).

## Hybrid Workload Patterns

### Document Writes + Relational Reads

```csharp
// Write via AeroDB (flexible document model)
await session.QueryAsync(
    "CREATE product SET name = $name, tags = $tags",
    new { name = "Widget", tags = new[] { "new", "featured" } }
);

// Read via EF Core (strongly-typed projections)
var featured = await dbContext.Products
    .FromSql($@"SELECT * FROM products WHERE tags CONTAINS 'featured'")
    .ToListAsync();
```

### Relational Writes + Document Reads

```csharp
// Write via EF Core
dbContext.Inventory.Add(new InventoryItem { Sku = "WIDGET-01", Quantity = 100 });
await dbContext.SaveChangesAsync();

// Read via AeroDB (for flexible document queries)
var product = await session.QueryAsync<Product>(
    "SELECT * FROM inventory WHERE sku = $sku",
    new { sku = "WIDGET-01" }
);
```

## Configuration and DI Registration

Register both providers in `Program.cs`:

```csharp
builder.Services
    .AddAeroDB("ws://localhost:8000", "aero", "shop")
    .AddDbContext<OrderDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));

// Optional: register the bridge for coordinated transactions
builder.Services.AddAeroDbExecutionStrategy();
```

## See Also

- [ASP.NET Identity](/docs/integrations/aspnet-identity)
- [Configuration](/docs/configuration)
