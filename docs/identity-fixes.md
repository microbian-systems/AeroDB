# AeroDB.AspNetIdentity — Bug Fixes & Schema Improvements

## Issue 1: `FindByEmailAsync` Returns `null` When User Exists

### Root Cause

In `ExpressionVisitor.cs`, the `Operand` method does not handle captured variables correctly.
When a lambda like `u => u.NormalizedEmail == normalizedEmail` is compiled to an expression tree,
the C# compiler wraps `normalizedEmail` in a closure. The expression tree becomes:

```
MemberExpression { Member = "normalizedEmail", Expression = ConstantExpression { Value = closureObj } }
```

`Operand` dispatches `MemberExpression` → `MemberPath()`, which returns the bare member name
(e.g. `"normalizedEmail"`) as a SurrealQL field reference — **not** the actual string value.
The generated query becomes:

```surql
SELECT * FROM identity_user WHERE NormalizedEmail = normalizedEmail LIMIT 1
```

SurrealDB interprets `normalizedEmail` as a field name comparison, which never matches any record.

### Fix

Modify both `Operand` overloads in `ExpressionVisitor.cs` to detect captured-variable
`MemberExpression` nodes and extract their actual values via `FieldInfo.GetValue`. This
follows the same pattern as the existing `TryExtractCollection` helper in the same file.

### Blast Radius

Fixes **all** store methods that capture parameters in LINQ predicates:
- `FindByEmailAsync`
- `FindByNameAsync`
- `FindByLoginAsync`
- `AddToRoleAsync`, `RemoveFromRoleAsync`, `IsInRoleAsync`
- `GetUsersInRoleAsync`
- Any user code using `session.Query<T>().Where(x => x.Field == localVar)`

### Files Changed

| File | Change |
|------|--------|
| `src/AeroDB/Linq/ExpressionVisitor.cs` | Add `TryExtractCapturedValue()` helper; guard both `Operand` overloads |

---

## Issue 2: Duplicate Email / Username Registrations

### Root Cause

Identity types (`TUser`, `TRole`) are never registered in `StoreOptions.Schema.For<T>()`,
so no `DEFINE INDEX` statements are issued. Additionally, `CreateAsync` does not check
for existing users before persisting.

### Fix — Three Layers

#### Layer 1: `StoreOptions.AddAeroIdentity<TUser, TRole>()` extension

New extension method on `StoreOptions` that registers default unique indexes. Called
**inside** `AddAeroDB`'s configure delegate, so indexes are created during
`DocumentStore.InitializeAsync()` — before any runtime queries.

```csharp
// src/AeroDB.AspNetIdentity/Extensions.cs
public static StoreOptions AddAeroIdentity<TUser, TRole>(this StoreOptions options)
    where TUser : IdentityUser
    where TRole : IdentityRole
{
    options.Schema.For<TUser>()
        .SetSchemaMode(SchemaMode.Flexible)
        .UniqueIndex(x => x.NormalizedUserName)
        .UniqueIndex(x => x.NormalizedEmail);
    options.Schema.For<TRole>()
        .SetSchemaMode(SchemaMode.Flexible)
        .UniqueIndex(x => x.NormalizedName);
    return options;
}
```

Usage:

```csharp
services.AddAeroDB(opts => {
    opts.Connection("ws://localhost:8000");
    opts.AddAeroIdentity<AppUser, IdentityRole>();

    // Optional: additional indexes on custom properties
    opts.Schema.For<AppUser>().UniqueIndex(x => x.SomeCustomField);
})
.AddDefaultIdentity<AppUser>(options => options.SignIn.RequireConfirmedAccount = true)
.AddRoles<IdentityRole>()
.AddAeroDBStores<AppUser, IdentityRole>();
```

The `where TUser : IdentityUser` constraint is enforced at compile time —
passing a type without `NormalizedEmail`/`NormalizedUserName` produces a CS0311 error.

#### Layer 2: Lazy `DEFINE INDEX` from the store (safety net)

Both `AeroDBUserStore` and `AeroDBRoleStore` lazily issue `DEFINE INDEX IF NOT EXISTS`
on first mutation call. This catches cases where the user forgets `AddAeroIdentity`.

| Store | Indexes Defined |
|-------|----------------|
| `AeroDBUserStore` | `NormalizedUserName UNIQUE`, `NormalizedEmail UNIQUE` |
| `AeroDBRoleStore` | `NormalizedName UNIQUE` |

#### Layer 3: Pre-create guard in `CreateAsync`

Before persisting a new user, check `FindByNameAsync` and `FindByEmailAsync`.
Wrap `SaveChangesAsync` in a try-catch for the SurrealDB unique constraint exception
to handle the race window between check and save.

### Files Changed

| File | Change |
|------|--------|
| `src/AeroDB.AspNetIdentity/Extensions.cs` | Add `AddAeroIdentity<TUser, TRole>()` on `StoreOptions` |
| `src/AeroDB.AspNetIdentity/AeroDBUserStore.cs` | Add `EnsureIdentitySchemaAsync()`; guard `CreateAsync` |
| `src/AeroDB.AspNetIdentity/AeroDBRoleStore.cs` | Add `EnsureRoleSchemaAsync()` |

---

## Implementation Order

1. **ExpressionVisitor.cs** — Fix captured-variable handling (unblocks `FindByEmailAsync`/`FindByNameAsync` used by the `CreateAsync` guard)
2. **Extensions.cs** — Add `AddAeroIdentity<TUser, TRole>()`
3. **AeroDBUserStore.cs** — Add lazy DDL + `CreateAsync` guard
4. **AeroDBRoleStore.cs** — Add lazy DDL for role table index
