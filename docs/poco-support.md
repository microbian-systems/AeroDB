# POCO Identity Support

> Register plain .NET types without inheriting from `IRecord`, `Record`, or `Entity<TId>`. Use any property as the SurrealDB record key.

**Last modified:** 2026-07-03

---

## Overview

POCO (Plain Old CLR Object) support allows you to register arbitrary .NET types with Dali — classes that don't derive from `IRecord`, `Record`, or `Entity<TId>`. Instead of coupling your model to Dali's base types, you designate a single property as the SurrealDB identity key.

Use cases:
- Light entities that don't need the full Dali base class contract
- Integrating existing domain models that can't inherit from Dali types
- Reducing ceremony when you only need CRUD on a simple type

```csharp
public class ProductSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}
```

## Configuration

Register a POCO and designate its identity property via `Schema.For<T>().Identity()`:

```csharp
using var store = DocumentStore.For(opts =>
{
    opts.Schema.For<ProductSummary>()
        .Identity(x => x.Id);
});
```

The `Identity<TProp>()` method tells Dali which property to use as the SurrealDB record key. Dali will:

1. Map `<TableName>:<IdentityValue>` to the native SurrealDB `RecordId` on save
2. Hydrate the identity property from the SurrealDB record key on load
3. Translate LINQ identity comparisons to native `WHERE id = ...` predicates

Configuration is validated eagerly — invalid identity types throw at registration time, not at runtime.

## Supported Identity Types

| Type | Example | Notes |
|---|---|---|
| `long` | `42` | Default — use with Snowflake IDs |
| `int` | `42` | |
| `ulong` | `42` | |
| `uint` | `42` | |
| `string` | `"abc123"` | |
| `Guid` | `Guid.NewGuid()` | `Guid.Empty` on save auto-generates a new Guid |
| `byte` | `(byte)1` | |
| `short` | `(short)1` | |
| `DateTime` | `DateTime.UtcNow` | Stored as ISO 8601 string in key |

### Unsupported Types

The following types are **not supported** as identity properties:

- `decimal`, `float`, `double` — floating-point identity is inherently unstable
- Complex types (structs, classes) — no reliable key serialization
- `byte[]`, arrays, collections — no deterministic string representation

Registering an unsupported identity type throws `ArgumentException` at configuration time.

## Identity Lifecycle

### Save

When `session.Store(poco)` is called, the identity property value is:
1. Extracted from the POCO before serialization
2. Converted to a SurrealDB `RecordId` (`ProductSummary:42`)
3. Written as a body field in the SurrealDB document (for query compatibility)

```
┌─────────────────────┐     ┌──────────────────────────────┐
│ ProductSummary      │     │ SurrealDB Record             │
│   Id     = 42       │ ──→ │ id      = ProductSummary:42  │
│   Name   = "Widget" │     │ Id      = 42                 │
│   Score  = 100      │     │ Name    = "Widget"           │
└─────────────────────┘     │ Score   = 100                │
                            └──────────────────────────────┘
```

### Load

When loading by identity, the identity property is hydrated from the native SurrealDB record key — not from the body field. This ensures the identity value is always authoritative:

1. SurrealDB returns `{ id: ProductSummary:42, Id: 42, Name: "Widget", Score: 100 }`
2. Dali extracts `42` from the `id` key and assigns it to `ProductSummary.Id`
3. The `Id` body field is ignored during deserialization

### LINQ Queries

Identity comparisons in LINQ are recognized and translated to native SurrealQL predicates:

```csharp
session.Query<ProductSummary>().Where(x => x.Id == 42)
```

Translates to:

```sql
SELECT * FROM ProductSummary WHERE id = 42
```

Only simple equality comparisons (`==`) on the configured identity property are optimized. Range queries (`>`, `<`) and compound conditions fall back to body-field filtering.

### Delete

`session.Delete(poco)` uses the identity property to construct the `RecordId`:

```
Delete(poco) → DELETE ProductSummary:42
```

This matches the same `RecordId` that was created during save.

## Current Limitations

| Limitation | Detail |
|---|---|
| **JSON round-trip** | Load and query deserialization uses a dictionary intermediate type with `System.Text.Json` round-trip. There is no optimized CBOR deserialization path for POCOs. |
| **No source-generated shims** | Unlike `Entity<TId>` types, POCOs don't have source-generated CBOR shims. This is planned for a future release. |
| **Guid.Empty semantics** | On save, a `Guid` identity of `Guid.Empty` is auto-replaced with `Guid.NewGuid()`. You cannot store records with `Guid.Empty` as their identity. |
| **Bulk query performance** | The JSON round-trip per entity adds measurable overhead for bulk queries (10+ ms per 1000 entities). Consider `Entity<TId>` with its CBOR shim for high-throughput scenarios. |
| **No identity change tracking** | Changing the identity property after the first save creates a new record rather than updating the existing one. |
| **No edge/graph support** | POCO types cannot participate in `Edge<TFrom, TTo>` graph relationships. |

## Comparison: Entity&lt;TId&gt; vs Record vs POCO

| Aspect | `Record` | `Entity<TId>` | POCO |
|---|---|---|---|
| **Inheritance** | Inherits `SurrealDb.Net.Models.Record` | Inherits `Dali.Entity<TId>` | No base class required |
| **Identity config** | `RecordId?` (auto-assigned) | `Identity(x => x.Id)` | `Identity(x => x.Id)` |
| **CBOR deserialization** | Native (built into SurrealDb.Net) | Source-generated shim | JSON round-trip (dictionary + deserialize) |
| **Load performance** | Fastest | Fast | Slower (JSON overhead) |
| **Edge relationships** | ✅ | ❌ | ❌ |
| **Polymorphic hierarchy** | ✅ | ❌ | ❌ |
| **Source-gen metadata** | ✅ | ✅ | ❌ |
| **BulkInsert** | Fast | ~40% slower (Snowflake) | Slower (JSON per entity) |
| **Use when** | Full-featured documents, event sourcing, graph edges | Application-controlled IDs, CBOR performance | Simple models, external DTOs, minimal ceremony |

### When to Use Each

- **`Record`** — your default choice. Full Dali feature set, fastest deserialization, edge support.
- **`Entity<TId>`** — when you need application-controlled identity (Snowflake IDs) with good performance. Use for primary domain entities.
- **POCO** — when you can't or don't want to inherit from Dali base types. Good for DTOs, read models, existing domain objects. Accept the JSON round-trip cost.

## Example

```csharp
using Dali;

public class ProductSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
}

var store = DocumentStore.For(opts =>
{
    opts.Schema.For<ProductSummary>().Identity(x => x.Id);
    opts.ConnectionString = "memory";
});

var session = await store.OpenSessionAsync(new SessionOptions());

// ── Save ──────────────────────────────────────────────────
var product = new ProductSummary { Id = 42, Name = "Widget", Score = 100 };
session.Store(product);
await session.SaveChangesAsync();

// ── Load ──────────────────────────────────────────────────
var loaded = await session.LoadAsync<ProductSummary>(42);
Console.WriteLine(loaded?.Name); // Widget

// ── Query ─────────────────────────────────────────────────
var results = await session.Query<ProductSummary>()
    .Where(x => x.Score > 50)
    .ToListAsync();

// ── Update ────────────────────────────────────────────────
loaded.Name = "Super Widget";
session.Store(loaded);
await session.SaveChangesAsync();

// ── Delete ────────────────────────────────────────────────
session.Delete(loaded);
await session.SaveChangesAsync();
```

## Implementation Details

POCO identity support uses a **dictionary-based intermediate type** for deserialization:

1. The identity property is configured and stored in `DocumentMapping`
2. On save, the identity value is extracted via reflection into the `RecordId`
3. On load/query, SurrealDB results are deserialized to `Dictionary<string, object?>` then round-tripped through `System.Text.Json` into the target POCO type
4. The identity property is then overwritten from the SurrealDB record key to guarantee correctness

The dictionary+JSON approach is a deliberate trade-off: it avoids the complexity of runtime IL emit or required source generation for POCOs, at the cost of per-entity JSON serialization overhead. A source-generated CBOR shim for POCOs is planned to close this gap.

See [architecture.md](architecture.md) for overall Dali architecture and [entity-feature-matrix.md](entity-feature-matrix.md) for how POCO support fits into the broader type model.
