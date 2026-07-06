---
title: Schema Management
description: Manage database schemas with AeroDB
---

## Schema Modes: Flexible vs Strict

| Mode | Behavior |
|------|----------|
| **Flexible** (`SCHEMALESS`) | Default. Any field accepted; missing fields default to `NONE`. |
| **Strict** (`SCHEMAFULL`) | Every field must match its definition. Extra/missing fields cause errors. |

```csharp
opts.Schema.For<User>()
    .SchemaMode(SchemaMode.Strict)
    .Field(u => u.Email, cfg => cfg.IsRequired().Unique());
```

## Defining Tables and Fields

Tables are auto-defined when `InitializeAsync` runs:

```csharp
opts.Schema.For<Order>()
    .Identity(o => o.OrderId)
    .Field(o => o.Total, cfg => cfg.WithType("decimal"))
    .Field(o => o.ShippedDate, cfg => cfg.IsNullable());
```

.NET to SurrealDB type mapping:

| .NET Type | SurrealDB Type |
|-----------|----------------|
| `string` | `string` |
| `int` / `long` | `int` |
| `decimal` | `decimal` |
| `DateTime` | `datetime` |
| `bool` | `bool` |
| `float` / `double` | `float` |
| `T?` | `option<T>` |

## Per-Type Schema Configuration

Different document types can use different modes:

```csharp
opts.Schema.For<Page>().SchemaMode(SchemaMode.Flexible);   // user content
opts.Schema.For<Invoice>().SchemaMode(SchemaMode.Strict)    // financial data
    .Field(i => i.Amount, cfg => cfg.IsRequired());
```

## Evolving Schemas (Migrations)

AeroDB's schema differ generates `DEFINE FIELD` only for new fields. Adding a nullable field is safe:

```csharp
opts.Schema.For<User>().Field(u => u.PhoneNumber, cfg => cfg.IsNullable());
```

Removing a field from the mapping does **not** drop it from SurrealDB. Drop explicitly:

```csharp
opts.Migration.AddMigration(new DropFieldMigration("user", "deprecated_field"));
```

## Validation Behavior

Validation occurs at two levels: AeroDB client-side (nullability, types) and SurrealDB server-side (SCHEMAFULL tables enforce field types on write). For SCHEMALESS tables, only client-side validation applies.

## Multi-Database Partitioning with .Schema()

Partition schemas across SurrealDB databases:

```csharp
var tenantA = store.Schema("tenant_a");
var tenantB = store.Schema("tenant_b");
tenantA.Schema.For<User>().Index(u => u.Email, c => c.IsUnique());
tenantB.Schema.For<User>().Index(u => u.Email, c => c.IsUnique());
```

## SCHEMAFULL vs SCHEMALESS

- **SCHEMALESS**: Accepts any fields. Ideal for polymorphic data, logs, or early development.
- **SCHEMAFULL**: All fields explicitly defined. Recommended for production domains with stable shapes (invoices, users, orders).

> **Tip**: Start with `SCHEMALESS` during development and promote to `SCHEMAFULL` once the shape is stable. AeroDB's schema differ handles the transition.
