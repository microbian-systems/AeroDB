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
4. Support graph operations (Relate, Graph traversal, Delete with edge cleanup) via explicit RecordId construction

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

### Graph Operations

POCO nodes can participate in SurrealDB graph operations. Edge types (the relationships) must still inherit from `EdgeRecord`, but the connected nodes can be POCOs.

#### Creating relationships with Relate

Since POCOs don't have a `RecordId` property, construct one explicitly using `RecordIdOf<string>`:

```csharp
var table = MetadataDispatch.GetTableName(typeof(PocoPerson));
var fromId = new RecordIdOf<string>(table, person.Id.ToString());
var toId = new RecordIdOf<string>(table, book.Id.ToString());

session.Relate<PocoWrote>(fromId, toId);
await session.SaveChangesAsync();
```

#### Graph traversal

POCOs work with `session.Graph<T>()` for traversal queries:

```csharp
// Out: find all books an author wrote
var books = await session.Graph<PocoPerson>()
    .Where(p => p.Name == "Author Name")
    .Out<PocoBook>("poco_wrote")
    .ToListAsync();

// In: find all authors of a book
var authors = await session.Graph<PocoBook>()
    .Where(b => b.Title == "Book Title")
    .In<PocoPerson>("poco_wrote")
    .ToListAsync();
```

#### Edge cleanup on node deletion

When a POCO node is deleted, SurrealDB automatically removes all edges pointing to or from that node:

```csharp
session.Delete(pocoPerson);
await session.SaveChangesAsync();

// All edges involving pocoPerson are cleaned up at the database level
var edges = await session.Query<PocoWrote>().ToListAsync();
// edges where pocoPerson is in or out are removed
```

#### Graph deduplication

When using `CollectAll()` in graph queries, Dali's `GraphQueryBuilder` uses `DeduplicateByIdentity` to remove duplicates. For POCOs, this reads the configured identity property (e.g., `Id`) to identify unique nodes. The deduplication key is resolved via a compiled delegate (one-time per type, zero per-element reflection).

#### Limitations

- Edge types must still inherit from `EdgeRecord` (POCO edges are not supported)
- The where-clause translation in `Graph<T>.Where()` uses a simplified expression translator that does not map POCO identity properties to the native `id` key. Use `RawQueryAsync` with explicit SurrealQL for identity-based graph filtering.
- Graph traversal tests pass against a real SurrealDB instance. The in-memory embedded engine has limited support for graph traversal syntax (`->edge->table`).

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
| **No source-generated edge shims** | POCO nodes can participate in graph operations, but edge types themselves must still inherit from `EdgeRecord`. |

## Comparison: Entity&lt;TId&gt; vs Record vs POCO

| Aspect | `Record` | `Entity<TId>` | POCO |
|---|---|---|---|
| **Inheritance** | Inherits `SurrealDb.Net.Models.Record` | Inherits `Dali.Entity<TId>` | No base class required |
| **Identity config** | `RecordId?` (auto-assigned) | `Identity(x => x.Id)` | `Identity(x => x.Id)` |
| **CBOR deserialization** | Native (built into SurrealDb.Net) | Source-generated shim | JSON round-trip (dictionary + deserialize) |
| **Load performance** | Fastest | Fast | Slower (JSON overhead) |
| **Edge relationships** | ✅ | ❌ | ✅ (see Graph Support) |
| **Polymorphic hierarchy** | ✅ | ❌ | ❌ |
| **Source-gen metadata** | ✅ | ✅ | ❌ |
| **BulkInsert** | Fast | ~40% slower (Snowflake) | Slower (JSON per entity) |
| **Use when** | Full-featured documents, event sourcing, graph edges | Application-controlled IDs, CBOR performance | Simple models, external DTOs, minimal ceremony |

### When to Use Each

- **`Record`** — your default choice. Full Dali feature set, fastest deserialization, edge support.
- **`Entity<TId>`** — when you need application-controlled identity (Snowflake IDs) with good performance. Use for primary domain entities.
- **POCO** — when you can't or don't want to inherit from Dali base types. Good for DTOs, read models, existing domain objects. Supports graph operations via explicit RecordId construction. Accept the JSON round-trip cost.

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

### Serialization / Materialization Pipeline

POCO deserialization avoids Dahomey.Cbor's converter system entirely. Instead, it reads raw CBOR directly and uses a cheap in-memory JSON round-trip to bridge unstructured dictionaries to typed objects:

```
SurrealDB SELECT response (CBOR binary)
    │
    ▼
CborResultReader.ReadPocoResult()          ← reads _binaryResult via reflection
    │                                          (bypasses Dahomey.Cbor.GetValue<T>())
    ▼
List<Dictionary<string, object?>>
    │
    ▼
NormalizePocoIdentityFields()               ← strips all native `id` entries from dicts,
    │                                          inserts `Id = 42` (correct CLR type)
    ▼
JsonSerializer.Serialize(records)           ← in-memory: CLR dictionaries → JSON string
    │                                          (no database call)
    ▼
JsonSerializer.Deserialize<List<T>>(json)   ← in-memory: JSON string → typed POCOs
    │                                          (no database call)
    ▼
List<PocoLong>  (ready for use)
```

Key points:
- The database is hit **once** (the `SELECT * FROM ...` query). Steps 2–4 are all in-memory.
- `CborResultReader.ReadPocoResult()` accesses `SurrealDbOkResult._binaryResult` via reflection to get the raw CBOR bytes, then uses `Dahomey.Cbor.CborReader` directly (not the converter system) to walk the CBOR tree and produce `Dictionary<string, object?>` entries.
- The JSON round-trip is purely a convenience mapper from `Dictionary<string, object?>` → `T`. The same memory is serialized and immediately deserialized — no network I/O.
- After deserialization, `SetPocoIdentityFromRecordId` overwrites the identity property from the SurrealDB record key (e.g., `"table:42"` → `Id = 42`) to guarantee correctness, since the body field `Id` may be missing or stale.

### Why not Dahomey.Cbor converters?

Dahomey.Cbor has no built-in converter for `Dictionary<string, object?>` or `List<object>` that handles SurrealDB's tagged types (RecordId tag 8, DateTime tag 12, UUID tag 37). A custom `CborMapToDictionaryConverter` exists in the SurrealDB SDK, but wiring it into Dahomey's type resolution for `GetValue<List<Dictionary<string, object?>>>(index)` is fragile: it conflicts with the SDK's own parameter dictionary serialization path. `CborResultReader` avoids this by owning the entire read pipeline.

### CborResultReader tag handling

For SurrealDB native types that appear in CBOR responses:

| CBOR Tag | SurrealDB Type | CborResultReader output |
|----------|---------------|------------------------|
| Tag 8    | RecordId (`["table", key]`) | `"table:key"` (string) |
| Tag 12   | DateTime (`[seconds, nanos]`) | `DateTime` |
| Tag 37   | UUID (16-byte string) | `Guid` |

These tagged values are converted to plain CLR types before the JSON round-trip, so `System.Text.Json` never sees the raw CBOR representation.

### Identity normalization before JSON

`NormalizePocoIdentityFields` in `InternalSessionBase.cs` runs **before** the JSON round-trip:

1. Finds all dictionary keys matching `"id"` (case-insensitive) and removes them
2. Extracts the SurrealDB record key string (`"table:42"`) from whichever `id` entry was present
3. Parses the key part (`"42"`) into the configured identity CLR type (`42L`)
4. Inserts a new dictionary entry `"Id"` → `42L` (using the actual `IdentityProperty` name)

This ensures `System.Text.Json` maps the identity value correctly to the POCO property without seeing the native RecordId string or array format.

### Why no global dictionary converter / provider

A Dahomey `CborConverterProvider` that handles `Dictionary<string, object?>` at the converter level is deliberately **not** registered. The SurrealDB SDK uses `Dictionary<string, object?>` internally for query parameter serialization (`RawQuery("...", new Dictionary<string, object?> { ... })`). A global converter provider for this type would:
- Risk intercepting parameter dictionaries and breaking parameter writes
- The SDK's `CborMapToDictionaryConverter.Write()` throws `NotSupportedException`
- Create a hard-to-debug dependency between POCO deserialization and query parameter routing

Instead, `CborResultReader` stays entirely inside Dali's own deserialization code and never touches the SDK's serialization paths.

### Performance note

The JSON round-trip adds ~10 ms per 1000 POCOs. For high-throughput scenarios, consider `Entity<TId>` with its source-generated CBOR shim (no JSON step). Source-generated CBOR shims for POCOs are planned for a future release.

See [architecture.md](architecture.md) for overall Dali architecture and [entity-feature-matrix.md](entity-feature-matrix.md) for how POCO support fits into the broader type model.
