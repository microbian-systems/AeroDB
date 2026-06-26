# Entity Types vs Record Types — Feature Matrix

## Overview

Dali supports two document type hierarchies:

| Base | ID Type | ID Management |
|---|---|---|
| `Record` (inherits `SurrealDb.Net.Models.Record`) | `RecordId?` | SurrealDB auto-assigns |
| `Entity<TId>` (inherits `Dali.Entity<TId>`) | `TId` (long, string, int, Guid) | Application-controlled (Snowflake for `long`) |

## Feature Compatibility

| Feature | Record | Entity&lt;TId&gt; | Notes |
|---|---|---|---|
| **Store / Save** | ✅ | ✅ | Both work identically |
| **LoadAsync&lt;T&gt;(id)** | ✅ | ✅ | Entity uses shim deserialization |
| **Query&lt;T&gt;()** | ✅ | ✅ | Full LINQ: Where, OrderBy, Select, etc. |
| **ToListAsync / FirstOrDefaultAsync** | ✅ | ✅ | Same query pipeline |
| **BulkInsertAsync** | ✅ | ✅ | Entity ~40% slower (Snowflake ID + explicit-ID parsing) |
| **Include &lt;forward&gt;** | ✅ | ⚠️ | Entity child FK must be compatible type |
| **IncludeReverse** | ✅ | ✅ | Works via `ExtractKeyString` string matching |
| **FilterInclude** | ✅ | ✅ | Same `Any()` / `Count()` predicates |
| **Schema.For&lt;T&gt;()** | ✅ | ✅ | Relaxed from `IRecord` → `class` constraint |
| **SetSchemaMode / Index / UniqueIndex** | ✅ | ✅ | Same API |
| **EnsureDocumentSchemaAsync** | ✅ | ✅ | Works for both |
| **Edge&lt;TFrom, TTo&lt;** | ✅ | ❌ | Requires `Record` — graph edges use `RecordId` |
| **HierarchyFor&lt;TBase&gt;()** | ✅ | ❌ | Polymorphic dispatch is Record-specific |
| **Source-gen metadata** | ✅ | ✅ | Both get `ITypeMetadata<T>` |
| **Source-gen CBOR shim** | ❌ (n/a) | ✅ | Shim enables CBOR deserialization for Entity types |

## How Entity Deserialization Works

1. `LoadAsync<EntityProduct>(id)` calls `Select<ProductShim>(rid)` where `ProductShim : Record`
2. SurrealDB returns CBOR, deserialized into the shim Record
3. `ToEntity()` maps shim properties → `EntityProduct` instance
4. Shim self-registers via `MetadataRegistry.RegisterShimType<TEntity>(typeof(Shim))`

## Known Limitations

- **No Edge relationships** — Entity types cannot participate in graph edge relations (`Edge<,,>`). Use Record types for graph relationships.
- **No polymorphic hierarchy** — `HierarchyFor<TBase>()` is Record-only. Entity types don't support polymorphic dispatch.
- **BulkInsert is slower** — ~40% overhead from Snowflake ID generation and SurrealDB explicit-ID parsing. Acceptable cost for most workloads.
- **IncludeReverse FK matching** — FK and parent Id are matched as strings via `ExtractKeyString`. Works for all TId types but may not handle custom/complex FK types.

## Migration Guide

### From Record → Entity&lt;long&gt;

```csharp
// Before
public class Product : Record
{
    public string Name { get; set; } = "";
}

// After
public class Product : EntitySnowlake  // Entity<long>
{
    public string Name { get; set; } = "";
}
```

### From Record → Entity&lt;string&gt;

```csharp
// Before
public class Customer : Record
{
    public string Name { get; set; } = "";
}

// After
public class Customer : EntityString  // Entity<string>
{
    public string Name { get; set; } = "";
}
```

## Constraint Relaxation (vNext)

These methods were relaxed from `where T : IRecord` to `where T : class`:

- `DocumentMapping<T>` constructor
- `Schema.For<T>()`
- `SchemaManager.EnsureDocumentSchemaAsync<T>()`
- `ISurrealDbQueryable.IncludeReverse<T, TChild>()`
- `ISurrealDbQueryable.FilterInclude<T, TChild>()`

This enables Entity types as first-class citizens in the schema, query, and include pipelines.
