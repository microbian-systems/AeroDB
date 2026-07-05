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

---

## Issue 2: Duplicate Email / Username Registrations

### Root Cause

Identity types (`TUser`, `TRole`) are never registered in `StoreOptions.Schema.For<T>()`,
so no `DEFINE INDEX` or `DEFINE TABLE` statements are issued. All identity tables are
schemaless with no unique constraints.

### Fix — Two Parts

#### Part A: `IConfigureAeroDB` via DI Discovery (Schema at Startup)

A new `AeroDBIdentityConfigurator<TUser, TRole> : IConfigureAeroDB` registers identity
schema configuration. It is registered from `AddAeroDBStores<TUser, TRole>()` as a
singleton in DI. During `DocumentStore.InitializeAsync()`, the existing
`ApplyDiscoveredConfigurators()` resolves it from DI and applies the schema —
**before** `DEFINE TABLE` and `DEFINE INDEX` are processed.

This uses the same infrastructure as `WolverineEnvelopeSchemas : IConfigureAeroDB`.

```csharp
internal sealed class AeroDBIdentityConfigurator<TUser, TRole> : IConfigureAeroDB
    where TUser : IdentityUser
    where TRole : IdentityRole
{
    public void Configure(IServiceProvider? services, StoreOptions options)
    {
        var identityOptions = services?.GetService<IOptions<IdentityOptions>>();
        var requireUniqueEmail = identityOptions?.Value?.User?.RequireUniqueEmail ?? true;

        options.Schema.For<TUser>()
            .UniqueIndex(x => x.NormalizedUserName);

        if (requireUniqueEmail)
            options.Schema.For<TUser>()
                .UniqueIndex(x => x.NormalizedEmail);

        options.Schema.For<TRole>()
            .UniqueIndex(x => x.NormalizedName);
    }
}
```

| Index | Always? | Why |
|-------|---------|-----|
| `NormalizedUserName` | ✅ Always | ASP.NET Core Identity always enforces this |
| `NormalizedEmail` | ✅ Only when `RequireUniqueEmail` | Respects the user's `IdentityOptions` setting |
| `NormalizedName` (role) | ✅ Always | Role names are always unique |

Schema mode defaults to `Strict` — `DocumentMapping<T>`'s default. `GetFieldSchemas(typeof(T))`
iterates all public readable/writable properties (inherited and custom), so subclass properties
like `AvatarUrl` get `DEFINE FIELD` automatically.

#### Part B: Inline Duplicate Guard in `CreateAsync`

Before persisting, check for existing users with the same `NormalizedEmail` or
`NormalizedUserName`. Wrap `SaveChangesAsync` in a try-catch for the unique constraint
error to handle the race window between check and save.

#### Usage

Types are specified **only once** (in `AddAeroDBStores`):

```csharp
services.AddAeroDB(opts => {
    opts.Connection("ws://localhost:8000");
    opts.Schema.For<AppUser>().UniqueIndex(x => x.SomeCustomField); // extras
})
.AddDefaultIdentity<AppUser>(options => {
    options.SignIn.RequireConfirmedAccount = true;
    options.User.RequireUniqueEmail = true; // controls whether unique email index is created
})
.AddRoles<IdentityRole>()
.AddAeroDBStores<AppUser, IdentityRole>();
```

#### What was NOT needed

| Approach | Verdict | Reason |
|----------|---------|--------|
| `PostConfigure<StoreOptions>` | ❌ Broken | `StoreOptions` isn't `IOptions`-backed; `PostConfigure` never fires |
| `IAeroDBSchemaInitializer` | ❌ Overengineered | Schema runs at startup via `IConfigureAeroDB`; duplicate check is a 15-line inline guard |
| `Lazy<Task>` / `SemaphoreSlim` | ❌ Not needed | `InitializeAsync()` already has its own single-flight state machine |
| `TKey` generic parameter | ❌ Deferred | Adds risk/scope to a critical fix; do as a separate feature later |

---

## Files Changed

| File | Change |
|------|--------|
| `src/AeroDB/Linq/ExpressionVisitor.cs` | Add `TryExtractCapturedValue()` helper; guard both `Operand` overloads |
| `src/AeroDB.AspNetIdentity/Extensions.cs` | Add `AeroDBIdentityConfigurator<TUser, TRole>`; register in `AddAeroDBStores` |
| `src/AeroDB.AspNetIdentity/AeroDBUserStore.cs` | Add inline duplicate check + try-catch in `CreateAsync` |
| `src/AeroDB.AspNetIdentity/AeroDBRoleStore.cs` | Add inline duplicate check + try-catch in `CreateAsync` |
