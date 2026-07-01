# Schema.For<T>() Validation Relaxation Options

## Problem

`DocumentMapping<T>.ValidateDocumentType()` at `src/Dali/Schema/DocumentMapping.cs:153` rejects any type registered via `Schema.For<T>()` that doesn't implement `IRecord` (SurrealDb.Net) or `IEntity<TId>` (Dali). This prevents users from registering plain POCOs as document types.

```csharp
public class Foo { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
o.Schema.For<Foo>();  // ❌ ArgumentException: not a valid document type
```

## Options

### Option A — Relax validation to allow any type with an Id property

Change `ValidateDocumentType<T>()` to accept any type that has a usable `Id` property (string or long). Remove the `Record`/`Entity` base class requirement.

**Implementation:**
```csharp
internal static void ValidateDocumentType<TDocument>()
{
    var type = typeof(T);
    if (type.IsAbstract)
        throw ...;

    var idProp = type.GetProperty("Id");
    if (idProp != null && (idProp.PropertyType == typeof(string) || idProp.PropertyType == typeof(long)))
        return;

    // Fallback: still accept IRecord / IEntity<TId>
    if (typeof(IRecord).IsAssignableFrom(type)) return;
    if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntity<>)))
        return;

    throw new ArgumentException(...);
}
```

**Pros:** Zero ceremony for simple POCOs. Matches user expectations. EventSourcingIntro's WarehouseProductReadModel would work directly.
**Cons:** `Record` provides `RecordId`-based operations (ToRecordId, equality by RecordId) that Dali identity map may rely on. Need to ensure `RecordId`-related code has a fallback path for non-Record types.

**Risk assessment:** Low-to-medium. Dali's identity map uses `Id` (string) as key, not `RecordId`. The SurrealDb.Net `Record` type is mainly for the native client's serialization, which Dali bypasses with its own pipeline.

---

### Option B — Add opt-in flag `SchemaOptions.AllowPlainPocos`

Add a configuration flag to `StoreOptions.Schema` that skips the base class check.

**Implementation:**
```csharp
public class SchemaOptions
{
    public bool AllowPlainPocos { get; set; }
    // ...
}

// In ValidateDocumentType:
if (!options.Schema.AllowPlainPocos)
{
    // existing strict validation
}
else
{
    // relaxed: check for Id property only
}
```

**Pros:** Backward compatible. Explicit opt-in. Clear intent in setup code.
**Cons:** Another configuration knob. Users have to remember to set it.

---

### Option C — Keep validation, improve error message

Leave validation as-is. Make the error message tell the user exactly what to do.

**Implementation:**
```csharp
throw new ArgumentException(
    $"Type '{type.FullName ?? type.Name}' is not a valid document type. " +
    $"Add ': IEntity<string>' to the class definition or inherit from {nameof(Record)}.");
```

**Pros:** Zero risk. No code changes to serialization pipeline.
**Cons:** Still requires ceremony. Doesn't solve the original complaint.

---

### Option D — Auto-register as schemaless

When `ValidationDocumentType` encounters a type without `Record`/`Entity<T>`, automatically register it in `SchemaMode.Flexible` (schemaless) instead of throwing. SurrealDB can store any JSON — no C# schema validation needed at the DB level.

**Implementation:**
```csharp
internal static void ValidateDocumentType<TDocument>()
{
    var type = typeof(T);
    if (type.IsAbstract) throw ...;

    // IRecord and IEntity<TId> are fine
    if (typeof(IRecord).IsAssignableFrom(type)) return;
    if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntity<>)))
        return;

    // Plain POCO: auto-assign Id as identity, set schemaless mode
    var idProp = type.GetProperty("Id");
    if (idProp != null && (idProp.PropertyType == typeof(string) || idProp.PropertyType == typeof(long)))
    {
        _schemaModeType = SchemaMode.Flexible;
        return;
    }

    throw new ArgumentException(...);
}
```

**Pros:** Zero-config. Auto-senses POCOs. Works out of box with sample apps and user code.
**Cons:** Silently changes schema mode. User may expect SCHEMAFULL but gets SCHEMALESS.

---

## Recommendation

**Option D** is the pragmatic winner: minimal code change, zero ceremony for users, automatic sane defaults. The schema mode change is actually desirable — plain POCOs should be schemaless by default anyway (you rarely want strict schema for simple document types).

Implementation outline:
1. Edit `DocumentMapping.cs` `ValidateDocumentType()` — ~5 lines added
2. Add test for `Schema.For<PlainPoco>()` where `PlainPoco` has `string Id`
3. Add test for `Schema.For<PlainLongIdPoco>()` where `PlainLongIdPoco` has `long Id`
4. Add test that abstract types still throw
5. Verify EventSourcingIntro and existing tests still pass

## Sample Impact

- **EventSourcingIntro**: `WarehouseProductWriteModel` could remain registered via `Schema.For<>()` if desired (but doesn't need to be)
- **MinimalAPI**: `User : IEntity<string>` model could drop the interface and be plain `User { string Id; string Name; string Email; }`
- **DocSamples**: QuestParty and Quest types could be plain POCOs
- **Helpdesk (future)**: Incident aggregate types would work without base classes
