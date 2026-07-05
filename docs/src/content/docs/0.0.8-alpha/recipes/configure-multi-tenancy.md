---
title: Configure Multi-Tenancy
description: Isolate data per tenant with database-per-tenant strategy
slug: 0.0.8-alpha/recipes/configure-multi-tenancy
---

## Goal

Run a single AeroDB instance that routes each tenant to its own SurrealDB database.

## Code

```csharp
using AeroDB;

// 1. Configure the store with a tenant selector
var store = DocumentStore.For(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Namespace("production");
    cfg.MultiTenantedWithDatabasePerTenant();

    // Map tenant IDs to databases
    cfg.DatabasePerTenant = tenantId => $"tenant_{tenantId}";
});

// 2. Open a session for a specific tenant
var tenantId = "acme-corp";
await using var session = store.LightweightSession(tenantId);

// 3. All operations are scoped to tenant_acme_corp
session.Store(new Order { CustomerId = "cust-1", Amount = 99.99m });
await session.SaveChangesAsync();

// Queries only see this tenant's data
var orders = await session.Query<Order>()
    .Where(o => o.Amount > 50)
    .ToListAsync();
```

## Explanation

* `MultiTenantedWithDatabasePerTenant()` enables database-level isolation — each tenant gets its own SurrealDB database.
* `DatabasePerTenant` maps tenant IDs (strings) to database names.
* `LightweightSession(tenantId)` opens a session scoped to one tenant.
* All queries, writes, and schema operations are automatically isolated.
* Alternative strategies: schema-per-tenant (`MultiTenanted` + `ConnectionPerTenant` hook).

## Tenant from HTTP Request

```csharp
// In ASP.NET Core middleware or minimal API
app.MapGet("/orders", async (HttpContext ctx, IDocumentStore store) =>
{
    var tenantId = ctx.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "default";
    await using var session = store.QuerySession(tenantId);
    return await session.Query<Order>().ToListAsync();
});
```

## See Also

* [Multi-Tenancy](/0.0.8-alpha/concepts/multi-tenancy/)
* [API: TenancyStyle](/0.0.8-alpha/api/AeroDB.TenancyStyle)
