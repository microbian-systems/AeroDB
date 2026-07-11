---
title: Schemas
description: Schema modes and configuration in AeroDB
---

AeroDB maps .NET types to SurrealDB tables and controls schema enforcement through the `SchemaMode` enum. Three modes — **Schemafull**, **Schemaless**, and **Flexible** — mirror SurrealDB's `SCHEMAFULL` / `SCHEMALESS` table directives, giving you fine-grained control over field validation.

## Schemafull

Schemafull (`SCHEMAFULL`) is the default mode in both SurrealDB and AeroDB. When a table is defined as `SCHEMAFULL`, only explicitly defined fields are permitted; any extra field in an `INSERT` or `UPDATE` operation is rejected at the database level.

In AeroDB, the default `SchemaMode` is `SchemaMode.Strict`, so every document type is schemafull unless you opt out:

```csharp
// Explicitly set (redundant — Strict is the default)
opts.Schema.For<User>().SetSchemaMode(SchemaMode.Strict);
```

The `DocumentMapping` default is `SchemaMode.Strict`, and the schema manager emits `DEFINE TABLE ... SCHEMAFULL` during initialization. You define fields via the fluent API:

```csharp
opts.Schema.For<User>()
    .Field("name", f => f.FieldType = "string")
    .Field("email", f =>
    {
        f.FieldType = "string";
        f.AssertExpression = "string::is::email($value)";
    })
    .Field("role", f =>
    {
        f.FieldType = "option<string>";
        f.DefaultValue = "'user'";
    });
```

**Required vs optional fields** are expressed through SurrealDB types. A field typed as `string` is required; `option<string>` allows `NULL`. AeroDB does not add a separate `DEFINE FIELD ... ASSERT $value != NONE` unless an explicit assertion is provided.

Schemafull is recommended for production domains where data integrity is critical — user profiles, billing records, inventory items — where an unexpected field likely signals a bug.

**Migration considerations:** Adding a new field to an existing `SCHEMAFULL` table is safe. Removing a field from the `FieldDefinition` list does not drop it from SurrealDB; the column remains in the table until you issue a `DEFINE FIELD ... DROP` SurrealQL statement manually.

## Schemaless

Schemaless (`SCHEMALESS`) allows any field on a table. No schema validation is performed — you can insert records with arbitrary properties and SurrealDB accepts them all.

In AeroDB, set schemaless mode on a per-type basis:

```csharp
opts.Schema.For<AuditLog>().SetSchemaMode(SchemaMode.Flexible);
```

The schema manager emits `DEFINE TABLE ... SCHEMALESS` for that type.

**When to use schemaless:**
- **Rapid prototyping** — iterate on document shapes without migration overhead
- **Flexible / polymorphic data** — logging payloads, metrics, webhook events where the shape varies per producer
- **Migration intermediate** — introduce a new document type as schemaless, then lock it down once the shape stabilizes

SurrealDB auto-creates tables on first write, so a schemaless document type works immediately without any `DEFINE TABLE` call:

```csharp
// No schema definition needed — the table is created on first INSERT
await session.StoreAsync(new AuditLog
{
    Action = "login",
    IpAddress = "192.168.1.1",
    Metadata = "{ \"browser\": \"Chrome\" }"
});
```

**Trade-offs:** Schemaless sacrifices type safety at the database layer. Invalid field names, typos, and unexpected data shapes are not caught until query time. It also prevents SurrealDB from applying field-level permissions or default values.

## Flexible

Flexible mode is a hybrid approach available through `SchemaMode.Flexible` in AeroDB. It maps to SurrealDB's `SCHEMALESS` table directive, but AeroDB still emits `DEFINE FIELD` statements for all registered field definitions. This gives you:

- **Partial schema enforcement** — core fields are typed, validated, and have defaults
- **Extra fields allowed** — non-registered fields are accepted without error
- **Gradual schema adoption** — start schemaless, define fields one by one as the model matures

```csharp
opts.Schema.For<AnalyticsEvent>()
    .SetSchemaMode(SchemaMode.Flexible)
    .Field("event_type", f => f.FieldType = "string")
    .Field("timestamp", f => f.FieldType = "datetime")
    .Field("payload", f => f.FieldType = "object");
```

With this configuration, `event_type`, `timestamp`, and `payload` are defined fields with types and defaults. Any additional properties (e.g., `device`, `experiment_id`) pass through unvalidated.

Flexible is ideal for:
- **Domain events** where certain properties are guaranteed and others are context-specific
- **Third-party integrations** where you control the core schema but the external payload varies
- **Feature flags / metadata** on otherwise strict documents

Edge tables default to `SchemaMode.Flexible` because graph relationships often carry dynamic properties:

```csharp
opts.Schema.Edge<Membership, User, Team>(edge =>
{
    edge.SetSchemaMode(SchemaMode.Flexible);
});
```

## Choosing a Mode

| Mode | Enum Value | Field Validation | Extra Fields | Use Case |
|------|------------|------------------|--------------|----------|
| Schemafull | `SchemaMode.Strict` | Full | Rejected | Core domain entities |
| Schemaless | `SchemaMode.Flexible` | None | Accepted | Prototyping, logs |
| Flexible | `SchemaMode.Flexible` | Defined only | Accepted | Hybrid / gradual adoption |

```csharp
opts.Schema.For<User>()
    .SetSchemaMode(SchemaMode.Strict);      // SCHEMAFULL — data integrity
opts.Schema.For<AuditLog>()
    .SetSchemaMode(SchemaMode.Flexible);    // SCHEMALESS — flexible payloads
opts.Schema.For<AnalyticsEvent>()
    .SetSchemaMode(SchemaMode.Flexible)     // Flexible — partial enforcement
    .Field("event_type", f => f.FieldType = "string");
```

> **Note:** Both `SchemaMode.Flexible` and full schemaless produce the same SurrealQL `SCHEMALESS` table directive. The difference is whether AeroDB emits `DEFINE FIELD` statements for your registered fields. For pure schemaless with no defined fields, omit all `.Field()` calls.
