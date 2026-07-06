---
title: Multi-Tenancy
description: Multi-tenant support in AeroDB
---

AeroDB supports two multi-tenancy strategies that isolate tenant data at different levels of the SurrealDB hierarchy.

## Tenancy Models

### Conjoined Tenancy (Row-Level)

All tenants share the same SurrealDB database. Tenant isolation is achieved through a `tenant_id` field on each document. Queries automatically filter by the current tenant.

```csharp
opts.AllDocumentsAreMultiTenanted();
// Sets TenancyStyle.Conjoined on all document mappings
```

### Database-per-Tenant

Each tenant gets their own SurrealDB database, created on first session access. The database name is derived from the namespace and tenant ID (e.g., `myns_tenant1`).

```csharp
opts.MultiTenantedDatabases();
// Sets TenancyStyle.DatabasePerTenant
```

### Per-Entity Configuration

Tenancy can be configured per entity type instead of globally:

```csharp
opts.Schema.For<User>().MultiTenanted();

// Or via policies:
opts.Policies.ForDocumentsOfType<Invoice>()
    .TenancyStyle = TenancyStyle.Conjoined;
```

## Working with Tenants

### Tenant-Aware Sessions

Create a session scoped to a specific tenant:

```csharp
// Database-per-tenant: WithTenant() routes to the tenant's database
await using var session = await store.WithTenant("acme-corp")
    .LightweightSessionAsync();

// Conjoined: ForTenant() sets the tenant filter
await using var session = await store.OpenSessionAsync();
session.ForTenant("acme-corp");
```

### How Tenant Filters Work

In **conjoined** mode, AeroDB injects a `tenant_id` filter into every query. When calling `Store<T>()`, the session automatically sets the `TenantId` property on the entity. On `LoadAsync<T>()`, the loaded document's tenant is verified against the session's tenant — if they don't match, null is returned.

In **database-per-tenant** mode, isolation is at the SurrealDB connection level. Each tenant gets a separate `ISurrealDbClient` instance managed by `DatabasePerTenantSelector`. No entity-level tenant fields are needed.

### Bulk Operations

```csharp
// Bulk insert for a specific tenant
await store.BulkInsertAsync("acme-corp", users);

// Tenant-scoped sessions
await using var idSession = await store.IdentitySessionAsync("acme-corp");
await using var dirtySession = await store.DirtyTrackedSessionAsync("acme-corp");
```

## Use Cases

- **SaaS applications** — Each customer (tenant) gets isolated data with minimal overhead
- **Multi-org platforms** — Organizations share infrastructure but data is strictly partitioned
- **Dev/staging isolation** — Database-per-tenant naturally separates environments on the same SurrealDB instance
- **Regulatory compliance** — Conjoined tenancy simplifies data export/deletion for GDPR requests by querying on `tenant_id`
