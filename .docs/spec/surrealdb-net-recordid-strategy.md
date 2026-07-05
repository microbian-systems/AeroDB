# SurrealDB.Net RecordId Strategy

## Record ID Types in SurrealDB

SurrealDB supports multiple types for the `id` portion of a record:

| Type | Example | Verified |
|------|---------|----------|
| `string` | `person:tobie` | ✓ |
| `number` (int/long) | `person:100` | ✓ |
| `UUID` | `person:550e8400-...` | ✓ (requires escaping via `⟨...⟩` or backticks) |
| `array` (composite) | `order_item:["order_1","product_1"]` | ✓ |

### Verifying Numeric (Long/Snowflake) IDs

Tested against SurrealDB MCP (in-memory engine):

```surql
-- Create record targeting a numeric ID directly
CREATE product:1700000000000000001 CONTENT {
    name: "Widget Alpha",
    price: 29.99
};

-- Include id in CONTENT
CREATE product CONTENT {
    id: 1700000000000000002,
    name: "Widget Beta"
};
```

Both produce `id: Number(1700000000000000002)` — the engine stores the value as `Number(Int)`.

### Constraining the ID Type via Schema

```surql
DEFINE TABLE customer SCHEMAFULL;
DEFINE FIELD id ON TABLE customer TYPE number;
DEFINE FIELD name ON TABLE customer TYPE string;
```

- `TYPE number` accepts all numeric subtypes (int, float, decimal)
- `TYPE long` is **NOT** a valid SurrealQL type — use `TYPE number` instead
- Attempting a string ID on a number-constrained table is rejected at write time:

```
CREATE customer:abc CONTENT { name: "Bad" };
→ FieldCheck: expected number, got 'abc'
```

### Caveat: WHERE on id

`WHERE id = 1700000000000000002` may fail because `id` is a `Thing { tb, id }` object, not a raw number. Use **direct fetches** for PK lookups:

```surql
SELECT * FROM product:1700000000000000002;  -- fast path (217µs)
```

## SurrealDB.Net Client Library Analysis

### Type Hierarchy

```
IRecord           (interface: RecordId? Id)
  ↓
Record            (abstract class implementing IRecord)
  ↑
IRelationRecord   (interface: In, Out RecordIds)
  ↓
RelationRecord    (abstract class extending Record)
```

**File**: `SurrealDb.Net/Models/Record.cs`

### RecordId Type

`RecordId` represents the `table:id` pair. Generic variant:

```csharp
public class RecordIdOf<TId> : RecordId
{
    public TId Id { get; private set; }
    public RecordIdOf(string table, TId id) : base(table) { ... }
}
```

Concrete specializations and factory methods exist for common types:

```csharp
RecordId.From(string table, string id)    // RecordIdOfString
RecordId.From(string table, int id)       // RecordIdOf<int>
RecordId.From(string table, long id)      // RecordIdOf<long>
RecordId.From(string table, short id)     // RecordIdOf<short>
RecordId.From(string table, byte id)      // RecordIdOf<byte>
RecordId.From(string table, Guid id)      // RecordIdOf<Guid>
RecordId.From<TId>(string table, TId id)  // generic fallback
```

Implicit tuple conversions also work:

```csharp
RecordId id = ("person", "jaime");
RecordId id = ("product", 42L);
```

### Method Constraints

The library has a split API design:

#### Methods requiring `IRecord` (POCOs NOT allowed — must inherit Record)

These methods extract table+id from the `IRecord.Id` property:

```csharp
Task<T> Create<T>(T data) where T : IRecord;
Task<T> Update<T>(T data) where T : IRecord;
Task<T> Upsert<T>(T data) where T : IRecord;
Task<TMerge> Insert<T>(string table, IEnumerable<T> data) where T : IRecord;
```

#### Methods accepting POCOs (no `IRecord` constraint)

These require explicit table name parameter (id is either auto-generated or managed via SurrealQL):

```csharp
Task<T> Create<T>(string table, T? data);
Task<T> Update<T>(string table, T data) where T : class;
Task<T> Upsert<T>(string table, T data) where T : class;
Task<List<T>> Select<T>(string table);
Task<List<T>> Select<T>(RecordId id);
```

### Serialization

- **Wire protocol**: CBOR via `Dahomey.Cbor`
- **Naming convention**: Preserves C# PascalCase property names as-is (no camelCase conversion)
- **Custom naming**: `[Column("field_name")]` attribute supported

## Non-Generic Converter Limitation (Cannot Patch)

surrealdb.net's `RecordIdConverter` hardcodes `ReadInt32()` + `RecordIdOf<int>`:

**File**: `surrealdb.net/SurrealDb.Net/Internals/Cbor/Converters/RecordIdConverter.cs:60-62`

```csharp
CborDataItemType.Signed or CborDataItemType.Unsigned => new RecordIdOf<int>(
    table, idReader.ReadInt32()
),
```

Snowflake IDs exceed `int.MaxValue`, so `ReadInt32()` **throws**. The `_serializedCborId` field is `null` on the resulting `RecordIdOf<int>` — `DeserializeId<long>()` cannot rescue it.

This file is in a **git submodule** and cannot be modified by Dali. The fix must live **above** surrealdb.net.

## Council Verification: Entity<long> End-to-End Analysis

An existing consumer uses `Product : Entity<long>` (with plain `long Id`, no `Record` inheritance). Multi-inheritance is not allowed, so `Record` cannot also be inherited.

### Entity Definition

```csharp
public interface IEntity<TId>
{
    TId Id { get; set; }
}
public abstract class Entity<TId> : IEntity<TId>
{
    public TId Id { get; set; }
}
public abstract class Entity : Entity<long> { }
```

**Zero dependencies** on surrealdb.net types — no `Record`, no `IRecord`, no `RecordId`.

### Per-Operation Verdict

| Operation | Status | Root Cause |
|-----------|--------|------------|
| **CREATE** (explicit Snowflake ID) | ❌ **Fails** | Line 287 requires `is IRecord` + non-empty ID. Entity<long> has non-empty Id but `is IRecord` is false → falls to auto-generate path. Explicit ID lost. Return deserialization also throws `CborException` (CBOR array → `long` mismatch). |
| **UPDATE** | ✅ **Works** | Uses `Merge<object>(RecordId, Dictionary)` — no deserialization. |
| **DELETE** | ✅ **Works** | Calls `Delete(RecordId)` — no deserialization. |
| **LoadAsync** | ⚠️ **Silent null** | `Select<T>(RecordId)` throws `CborException`, caught empty, returns `null`. Caller sees "not found". |
| **Query<T>** | ❌ **Fails** | `response.GetValue<List<T>>(0)` throws `CborException` — no catch, propagates unhandled. |

### Root Cause: CBOR Type Mismatch

```
SurrealDB CBOR output:  id → CBOR Tag(recordid) ["product", 12345]  (CBOR array)
Entity<long> property:  public long Id { get; set; }                (expects CBOR number)
```

Dahomey.Cbor cannot bridge this gap. The `RecordIdConverter` and `RecordIdOfTConverter<T>` only handle `RecordId`/`RecordIdOf<T>` typed properties. There is **no converter for `long`** when the CBOR value is an array.

The `[CborProperty("id")]` annotation on `IRecord.Id` triggers the RecordId converter chain **only when the CLR type is `RecordId?`**. Adding it to a `long` property does not help — a converter for the CBOR array format is still missing.

### Why CREATE Writes Succeed Server-Side But Still Fail

1. `Create<T>(table, entity)` sends `{id: 12345, name: "Widget"}` via CBOR → SurrealDB creates `product:12345` ✅
2. SurrealDB returns `{id: ["product", 12345], name: "Widget"}`
3. `session.Create<T>()` calls `dbResponse.GetValue<T>()` → `CborSerializer.Deserialize<T>(...)`
4. Dahomey.Cbor sees `id` as CBOR array, property is `long` → throws `CborException` ❌
5. Exception propagates through `CreateEntityAsync` → `SaveChangesAsync` → caught and wrapped as `InvalidOperationException`

### The CREATE Condition Bug

**File**: `DocumentSession.cs:287`

```csharp
if (!string.IsNullOrEmpty(entityId) && op.Entity is IRecord rec)
```

For Entity<long> with explicit `Id = 12345`:
- `entityId = "12345"` (non-empty ✅)
- `op.Entity is IRecord` → **false** ❌

Falls to the ELSE branch (auto-generate path), then fails on return deserialization.

### Source Generator Gap

**File**: `DaliDocumentGenerator.cs:45`

```csharp
if (!IsRecordSubclass(type, recordType)) continue;
```

Entity<long> types are skipped entirely. They get no generated metadata. The reflection fallback in `MetadataDispatch` works correctly for table name, entity ID, and tenant/version lookups. This is a **performance-only** gap — not a correctness issue for save/read.

## Implementation Plan

### Fix 1: CREATE Path — Route IEntity<TId> with Explicit IDs to Merge

**File**: `DocumentSession.cs`, line ~287

**Problem**: The current condition requires both a non-empty entity ID AND the entity to implement `IRecord`. For `Entity<long>` entities, `entityId` is non-empty (a Snowflake string) but `is IRecord` is false, so they fall into the auto-generate `CreateEntityAsync` path which:
1. Ignores the explicit ID — SurrealDB assigns a new auto-ID
2. Deserializes the return response through `CborSerializer.Deserialize<T>()` which throws because CBOR array `["product", 12345]` cannot map to `long Id`

**Approach**: Restructure the branch so the `is IRecord` check distinguishes only the serialization strategy, not the overall flow decision. All entities with explicit IDs should go through an upsert-style write.

**Steps**:

1. **Modify `DocumentSession.cs`** in three places (Added, Modified, and the general `SaveChangesAsync` entry):
   - **OperationType.Added**: Change from the two-branch pattern (IRecord vs CreateEntityAsync) to a unified check for non-empty entity ID, then branch on `is IRecord` only for serialization method selection
   - For non-IRecord entities with explicit IDs: serialize via `JsonSerializer` to `Dictionary<string, object>` (same as the existing UPDATE path at lines 322-327), then call `Merge<object>` with the RecordId
   - Remove the `CreateEntityAsync` fallback for entities with explicit IDs — only use it for entities where `GetEntityId` returns null/empty (truly new, auto-assigned ID)

2. **Verify the DELETE path** (line 332-337) — already works, no changes needed. It uses `GetRecordId` which falls through to `idValue.ToString()` for plain `long`/`string` Ids.

3. **Edge cases to consider**:
   - Entity with `Id = 0` (default long): `GetEntityId` returns `"0"` which is non-empty. This entity would enter the Merge path, which is correct if the ID is deliberately set to 0, but might be surprising if 0 is used as "not set". Need to decide: is `Id = 0` a valid explicit ID or should it be treated as "not set"? SurrealDB allows `person:0` as a valid numeric ID, so 0 is valid.
   - Entities auto-generated by the system (like projections from `RebuildAsync`): these have IDs already assigned by SurrealDB, so they correctly enter the Merge path.
   - Tenant ID or version properties on the entity: the JSON serialization includes all properties, so these are preserved in the Merge.

4. **Test scenarios**:
   - New entity with explicit Snowflake ID → stored at `table:{snowflake}`
   - New entity with no ID set (Id = 0) → decide behavior (auto-generate or treat as explicit 0)
   - Updated entity after load → existing UPDATE path already works
   - Soft-deleted entity → verify the soft-delete path at line 339+ handles Entity<long> correctly

### Fix 2: Read Path — Generic CBOR Converter for IEntity<TId>

**Problem**: surrealdb.net's CBOR layer has no converter for `long Id`, `int Id`, `string Id`, or `Guid Id` properties on non-IRecord types. SurrealDB returns the `id` field as a CBOR 2-element array `[table_name, id_value]`, but Dahomey.Cbor only knows how to deserialize this into `RecordId` or `RecordIdOf<T>` types — not into primitives like `long` or `string`.

**Approach**: Register a custom converter provider via surrealdb.net's `configureCborOptions` hook. The converter intercepts CBOR deserialization when the target property is an `Id` field on a type that implements `IEntity<TId>`. It reads the 2-element CBOR array, discards element [0] (table name), and deserializes element [1] to the target primitive type.

**Steps**:

1. **Identify the extensibility point**: surrealdb.net's `SurrealDbOptions` or equivalent has a `ConfigureCborOptions` delegate. This is used during `ISurrealDbClient` construction. Dali already controls client construction via its store configuration — confirm where this hook surfaces.

2. **Design the converter provider**: Create a class that implements Dahomey.Cbor's `ICborConverterProvider` interface. In its `GetConverter(Type type)` method:
   - Check if the target type is `IEntity<>` (or a subclass)
   - This is a converter provider, but Dahomey.Cbor converter providers are type-scoped, not property-scoped. The converter must be registered for primitive types (`long`, `int`, `string`, `Guid`) but only activate when the CBOR data item is a 2-element array — not when it's a simple value.
   - The provider needs to return a converter that inspects the CBOR data item at runtime: if it's a 2-element array, handle it as a SurrealDB record ID; if it's a primitive value, delegate to the default converter.

   Alternative approach: instead of a converter provider (which is type-scoped and complex to implement correctly), use a **fallback converter** for `object` or a custom marker interface.

   **Preferred approach** — simpler and more robust: use a **generic wrapper type for deserialization** (no cbor converter at all):

3. **Alternative to converter: RecordIdOf<T> shims via source generator** (recommended over converter approach):
   - Instead of registering a custom CBOR converter, the source generator (Fix 3) emits a parallel deserialization type per entity. This type uses `RecordIdOf<T>? Id` (which triggers surrealdb.net's built-in generic converter, which already handles Int64 correctly).
   - The materializer then copies `shim.Id!.Id` → `entity.Id` after deserialization.
   - This avoids any CBOR framework customization entirely. It is zero-risk and uses only surrealdb.net's existing, tested converters.
   - The tradeoff: source generation adds build-time complexity. For the initial implementation, this is the recommended path since it doesn't touch serialization at all.

4. **If the converter approach is chosen instead**:
   - The converter must handle READ (CBOR array `[table, value]` → primitive) and WRITE (primitive → CBOR number/string, since the table name is in the SurrealQL target, not in the CBOR payload)
   - **READ**: parse the CBOR array, skip element 0, deserialize element 1 as the target type
   - **WRITE**: serialize the primitive value directly (the `id` CBOR field should be just the number/string, not a full RecordId array, because Dali sends `CREATE table:{id} CONTENT {...}` where the ID is in the target, not the content)
   - Edge: `Guid` may need special handling since SurrealDB can store UUIDs and the CBOR representation may differ
   - Edge: SurrealDB's non-generic converter uses `ReadInt32()` — but this custom converter will use `ReadInt64()` and promote to `long`, bypassing the SDK's limitation entirely
   - Edge: `null` / `default` / missing `id` field in the response

5. **Test scenarios**:
   - `List<Entity<long>>` returned from query → each entity has correct `Id`
   - `Entity<string>` with string IDs → works without converter (string→string is a direct CBOR match)
   - `Entity<Guid>` → verify UUID CBOR format used by SurrealDB
   - Empty result set → no crash
   - Mixed result with Record subclass entities and Entity<long> entities → both work

### Fix 3: Source Generator — Extend for IEntity<TId> Types

**File**: `DaliDocumentGenerator.cs`, around line 45

**Problem**: The source generator currently filters to only `Record` subclasses:
```csharp
if (!IsRecordSubclass(type, recordType)) continue;
```
This means `Entity<long>` types get no generated metadata — no `GetRecordId` accessor, no `TableName` in `MetadataRegistry`. Everything falls back to reflection, which is correct but slower.

**Approach**: Add a second filter for types implementing `IEntity<TId>`, and generate the same metadata shims (with a slightly different implementation that uses reflection-free `long Id` / `string Id` access instead of `RecordId? Id`).

**Steps**:

1. **Detect IEntity<TId> implementations**: For each candidate type in the compilation, check if it implements `IEntity<TId>` (via `INamedTypeSymbol.AllInterfaces`). The source generator already has access to the compilation and can resolve `IEntity<>` by metadata name (same pattern as the existing `Record` resolution).

2. **Generate metadata for IEntity<TId> types**:
   - `GetRecordIdAccessor`: Instead of extracting `RecordId?` (which `Record` subclasses have), extract the primitive `Id` value and return `entity.Id.ToString()`. This eliminates the reflection fallback in `GetEntityId`.
   - `TableName`: Generate the same snake_case table name from the type name.
   - Tenant/version accessors: Same logic as Record subclasses.
   - The generated code needs to handle both `Entity<long>` and `Entity<string>` generically.

3. **Register in MetadataRegistry**: The generated initialization code calls `MetadataRegistry.Register<T>()` with the same accessor delegates, just implemented differently for primitive access.

4. **Edge cases**:
   - `Entity<int>` — less common, but possible. `.ToString()` works the same.
   - `Entity<Guid>` — same pattern.
   - Nested or derived entity types (e.g., `class Product : Entity<long>` where `Product` is not sealed).
   - Generic entities? Unlikely but should not crash.

5. **Interaction with other generators**: The current generator also generates `DaliDocument` classes and `AddDaliDocument<T>` registration extensions. For `IEntity<TId>` types, these should also be generated — the `AddDaliDocument<T>` call registers the type with the store, which is needed for tracking.

6. **Test scenarios**:
   - Verify generated code compiles for `Entity<long>`, `Entity<string>`, `Entity<Guid>` types
   - Verify `MetadataRegistry` has the registered accessors (not null) for generated types
   - Verify non-generated types still fall back to reflection gracefully
   - Verify source generator incremental correctness — re-running on type change updates the generated file

### Fix D: Integration and Sequencing

The three fixes have dependencies:

```
Fix 3 (source gen) ──────┐
                          ├──> Both Fix 1 and Fix 2 benefit from generated metadata,
                          │    but neither strictly depends on it (reflection fallback exists)
                          │
Fix 1 (CREATE path) ──────┼──> Can be tested independently with current reflection
                          │    Save path is fully functional without source gen
                          │
Fix 2 (read/query path) ──┘──> Can use either converter approach (independent)
                               or RecordIdOf<T> shims from Fix 3 (preferred)
```

Recommended order:
1. Fix 3 (source generator) — enables everything, zero risk (additive only)
2. Fix 1 (CREATE path) — unblocks saves with explicit IDs
3. Fix 2 (read/query path) — uses shims from Fix 3 for clean deserialization

### Supported ID Types Matrix

After all three fixes, Dali will support these entity patterns transparently:

| User entity | Id type | CREATE (explicit ID) | UPDATE | DELETE | LoadAsync | Query |
|---|---|---|---|---|---|---|
| `class P : Record` | `RecordId?` | ✅ Upsert | ✅ Upsert | ✅ Delete | ✅ | ✅ |
| `class P : Entity<long>` | `long` | ✅ Merge | ✅ Merge | ✅ Delete | ✅ Shim/Rid | ✅ Shim/Rid |
| `class P : Entity<string>` | `string` | ✅ Merge | ✅ Merge | ✅ Delete | ✅ Shim/Rid | ✅ Shim/Rid |
| `class P : Entity<int>` | `int` | ✅ Merge | ✅ Merge | ✅ Delete | ✅ Shim/Rid | ✅ Shim/Rid |
| `class P : Entity<Guid>` | `Guid` | ✅ Merge | ✅ Merge | ✅ Delete | ✅ Shim/Rid | ✅ Shim/Rid |

All patterns assume `IEntity<TId>` with a single `Id` property matching the type parameter. The source generator handles all four variants identically (the shim uses `RecordIdOf<long>`, `RecordIdOf<string>`, etc., based on the generic parameter).

## End-to-End Architecture After Fixes

```
User entity:     Product : Entity<long> { long Id; string Name; }
                       │
DocumentSession: Condition at line 287 now routes Entity<long>
                 with explicit IDs to Merge path (no deserialization)
                       │
SurrealDB:       UPSERT product:1700000000000000001
                       │
Response CBOR:   { id: ["product", 1700000000000001], name: "Widget" }
                       │
Custom CBOR      Sees `id` is CBOR array and target is `long`
Converter:       Extracts index [1] as `long` → sets entity.Id
                       │
Entity:          Product { Id = 1700000000000000001, Name = "Widget" }
```

## Why Not Inherit Record?

| Concern | Why |
|---------|-----|
| **Coupling** | Forces `Entity<long>` to depend on surrealdb.net types |
| **Id typing** | `Record.Id` is `RecordId?`, not `long` — adds casting everywhere |
| **Converter** | Non-generic converter uses `ReadInt32()` — broken for Snowflake IDs |
| **EF Core conventions** | EF expects `long Id` for PK; `RecordId?` breaks the pattern |

## Verified References

- SurrealDB MCP verification (in-memory engine, June 2026)
- `surrealdb.net` source at `./surrealdb.net/SurrealDb.Net/Models/`
- Dali source at `./src/Dali/DocumentSession.cs`
- Source generator at `./src/Dali.SourceGenerators/DaliDocumentGenerator.cs`
- Linq query provider at `./src/Dali/Linq/SurrealQueryProvider.cs`
- EF Core plan at `../.docs/archive/DEPRECATED-efcore-plan.md`
- Council verification (multi-LLM consensus, June 2026)
