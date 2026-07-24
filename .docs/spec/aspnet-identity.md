# Dali.AspNetIdentity — SurrealDB-backed ASP.NET Core Identity Provider

## Overview

Port of `Marten.AspNetIdentity` to use Dali/SurrealDB as the backing store for ASP.NET Core Identity, with full .NET 10 passkey (WebAuthn) support. Uses generic `TKey` for primary key flexibility (string, long/Snowflake, Guid).

## Reference

- **Marten.AspNetIdentity** (code): `./Marten.AspNetIdentity/`
- **Marten.AspNetIdentity** (upstream): <https://github.com/yetanotherchris/Marten.AspNetIdentity>
- **.NET 10 Identity passkey docs**: <https://learn.microsoft.com/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0>
- **`IUserPasskeyStore<TUser>`**: <https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.iuserpasskeystore-1?view=aspnetcore-10.0>
- **`UserPasskeyInfo`**: <https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.identity.userpasskeyinfo?view=aspnetcore-10.0>

## Project: `src/Dali.AspNetIdentity/`

### Dependencies

- `Dali` (project reference)
- `Microsoft.Extensions.Identity.Core` 10.0.x
- `Microsoft.Extensions.Identity.Stores` 10.0.x
- `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.x
- `Microsoft.Extensions.Logging.Abstractions` 10.0.x

All versions via Central Package Management (`Directory.Packages.props`). Target only `net10.0`.

### Files

| File | Purpose |
|------|---------|
| `Dali.AspNetIdentity.csproj` | Project file |
| `Extensions.cs` | `AddDaliStores<TUser, TRole, TKey>()` DI extension on `IdentityBuilder` |
| `DaliUserStore.cs` | User store implementing all 15 applicable interfaces |
| `DaliRoleStore.cs` | Role store implementing `IRoleStore<TRole>` + `IQueryableRoleStore<TRole>` |

### Generic Architecture

```
DaliUserStore<TUser, TRole, TKey>
    where TUser : IdentityUser<TKey>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey>

DaliRoleStore<TRole, TKey>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey>
```

**Supported key types:**

| TKey | User Base | Role Base | Typical Use |
|------|-----------|-----------|-------------|
| `string` | `IdentityUser` (default) | `IdentityRole` (default) | Most web apps |
| `long` | `IdentityUser<long>` | `IdentityRole<long>` | Snowflake IDs |
| `Guid` | `IdentityUser<Guid>` | `IdentityRole<Guid>` | Distributed systems |

TKey values are converted to string for SurrealDB document IDs via `ToString()`: `$"user:{user.Id}"`.

**Built-in entity types used (no custom DaliXxx classes):**

| Identity Type | Used For |
|---------------|----------|
| `IdentityUserClaim<TKey>` | Claims (separate table) |
| `IdentityUserLogin<TKey>` | External logins (separate table) |
| `IdentityUserToken<TKey>` | Auth tokens (separate table) |
| `IdentityUserPasskey<TKey>` | WebAuthn passkeys (separate table, nested `IdentityPasskeyData`) |

### Table Naming

Table names are auto-derived from the CLR type name via snake_case conversion. Dali uses the same resolution internally for `session.Query<T>()`.

| Type | Table Name | Notes |
|------|------------|-------|
| `IdentityUser<TKey>` | `identity_user` | User documents |
| `IdentityRole<TKey>` | `identity_role` | Role documents |
| `IdentityUserClaim<TKey>` | `identity_user_claim` | Claim records |
| `IdentityUserLogin<TKey>` | `identity_user_login` | Login records |
| `IdentityUserToken<TKey>` | `identity_user_token` | Token records |
| `IdentityUserPasskey<TKey>` | `identity_user_passkey` | Passkey records |
| `HasRoleEdge` | `has_role` | Graph edges (user → role) |

### Storage Model

| Entity | Storage | Cascade on Delete | Query Style |
|--------|---------|-------------------|-------------|
| Core user properties | User document | Auto (document delete) | LINQ + fluent |
| AuthenticatorKey | Embedded field (raw SurrealQL) | Auto | Raw SQL via `IQuerySession` |
| RecoveryCodes | Embedded field (raw SurrealQL) | Auto | Raw SQL via `IQuerySession` |
| Claims | `identity_user_claim` table | Manual in `DeleteAsync` | `session.Query<IdentityUserClaim<TKey>>()` |
| Logins | `identity_user_login` table | Manual in `DeleteAsync` | `session.Query<IdentityUserLogin<TKey>>()` |
| Tokens | `identity_user_token` table | Manual in `DeleteAsync` | `session.Query<IdentityUserToken<TKey>>()` |
| Passkeys | `identity_user_passkey` table | Manual in `DeleteAsync` | LINQ + raw SQL (byte[] lookup) |
| Role membership | Graph edges (`has_role`) | **Auto-cascade** | `IGraphQuery<T>` fluent API |

### Why Graph Edges for Role Membership

SurrealDB's `RELATE` edges auto-cascade when the source or target record is deleted. This means when a user or role is deleted, all its `has_role` edges are automatically removed — no application cleanup needed.

**Operation mapping:**

| Store Method | API | Style |
|-------------|-----|-------|
| `AddToRoleAsync` | `session.RelateAsync<HasRole>(userRid, roleRid, ct)` | Fluent |
| `RemoveFromRoleAsync` | `session.ExecuteSqlAsync("DELETE has_role WHERE in=$u AND out=$r", ...)` | Raw (small) |
| `GetRolesAsync` | `store.Graph<TUser>().Where(...).Out<TRole>("has_role").ToListAsync()` | Fluent |
| `GetUsersInRoleAsync` | `store.Graph<TRole>().Where(...).In<TUser>("has_role").ToListAsync()` | Fluent |
| `IsInRoleAsync` | `store.Graph<TUser>().Where(...).Out<TRole>("has_role").CountAsync()` | Fluent |

### Store Interfaces

**DaliUserStore** implements:

| # | Interface | Purpose |
|---|-----------|---------|
| 1 | `IUserStore<TUser>` | Core CRUD |
| 2 | `IUserPasswordStore<TUser>` | Password hashes |
| 3 | `IUserEmailStore<TUser>` | Email |
| 4 | `IUserPhoneNumberStore<TUser>` | Phone |
| 5 | `IUserTwoFactorStore<TUser>` | 2FA toggle |
| 6 | `IUserAuthenticatorKeyStore<TUser>` | TOTP key (properly persisted) |
| 7 | `IUserTwoFactorRecoveryCodeStore<TUser>` | Recovery codes (properly persisted) |
| 8 | `IQueryableUserStore<TUser>` | `Users` property |
| 9 | `IUserClaimStore<TUser>` | Full claims (Type + Value) |
| 10 | `IUserLoginStore<TUser>` | External logins |
| 11 | `IUserRoleStore<TUser>` | Role membership |
| 12 | `IUserSecurityStampStore<TUser>` | Security stamp |
| 13 | `IUserLockoutStore<TUser>` | Lockout tracking |
| 14 | `IUserAuthenticationTokenStore<TUser>` | Auth tokens |
| 15 | `IUserPasskeyStore<TUser>` | Passkeys (WebAuthn) — .NET 10 new |

**DaliRoleStore** implements:

| # | Interface | Purpose |
|---|-----------|---------|
| 1 | `IRoleStore<TRole>` | Role CRUD |
| 2 | `IQueryableRoleStore<TRole>` | `Roles` property |

### DI Registration

```csharp
// String keys (default IdentityUser/IdentityRole)
services.AddIdentity<IdentityUser, IdentityRole>(options => { ... })
        .AddDaliStores<IdentityUser, IdentityRole, string>()
        .AddDefaultTokenProviders();

// Snowflake long keys
services.AddIdentity<IdentityUser<long>, IdentityRole<long>>(options => { ... })
        .AddDaliStores<IdentityUser<long>, IdentityRole<long>, long>()
        .AddDefaultTokenProviders();
```

Expects `IDocumentStore` already registered in DI as a singleton.

### Differences from Marten.AspNetIdentity

| Area | Marten.AspNetIdentity | Dali.AspNetIdentity |
|------|----------------------|---------------------|
| Key type | `string` only | Generic `TKey` (string, long, Guid) |
| Authenticator key | ❌ Stubbed (no-op) | ✅ Properly persisted |
| Recovery codes | ❌ Stubbed (always 5) | ✅ Properly persisted |
| External logins | ❌ Missing | ✅ Implemented |
| Role membership | ❌ Missing | ✅ Graph edges (auto-cascade) |
| Security stamps | ❌ Missing | ✅ Implemented |
| Lockout tracking | ❌ Missing | ✅ Implemented |
| Auth tokens | ❌ Missing | ✅ Implemented |
| Passkeys | ❌ Missing | ✅ Full .NET 10 |
| Claims | ⚠️ Role-only (`List<string>`) | ✅ Full Type/Value pairs |
| Entity types | Custom `IClaimsUser` | Built-in `IdentityUserClaim<TKey>` etc. |
| Role queries | N/A (unsupported) | Fluent `IGraphQuery<T>` traversal |

### Implementation Notes

- **Session lifetime**: Each store method opens a lightweight session via `_store.LightweightSessionAsync()` — consistent with Dali patterns. No session caching or pooling needed.
- **TKey→string**: SurrealDB document IDs are always strings. `$"{table}:{tkey}"` relies on `TKey.ToString()`. For parameterized queries, TKey values are passed as typed parameters directly.
- **Passkeys**: Stored in `identity_user_passkey` table with nested `IdentityPasskeyData`. Lookup by `byte[]` credential ID uses raw SurrealQL since byte array comparison isn't reliable via LINQ across all drivers.
- **Recovery codes**: Serialized as `string[]` on the user document via raw SurrealQL.
- **Authenticator key**: Stored as a single `string` field on the user document via raw SurrealQL.
- **Concurrency**: Uses `ConcurrencyStamp` property on `IdentityUser<TKey>`/`IdentityRole<TKey>` for optimistic concurrency.
- **Role edges**: SurrealDB automatically removes graph edges when the source or target node is deleted — `DeleteAsync` doesn't need to clean up roles.

## TKey Refactoring — What Changed

### Before (v1 — custom entity classes, string-only)

```csharp
public class DaliUserStore<TUser, TRole>
    where TUser : IdentityUser
    where TRole : IdentityRole
```

Custom classes: `DaliUserClaim`, `DaliUserLogin`, `DaliUserToken`, `DaliUserPasskey`  
Role storage: Embedded `role_ids` array on user doc (raw SQL)  
Registration: `AddDaliStores<IdentityUser, IdentityRole>()`

### After (v2 — built-in entity classes, generic TKey, graph edges)

```csharp
public class DaliUserStore<TUser, TRole, TKey>
    where TUser : IdentityUser<TKey>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey>
```

Built-in classes: `IdentityUserClaim<TKey>`, `IdentityUserLogin<TKey>`, `IdentityUserToken<TKey>`, `IdentityUserPasskey<TKey>`  
Role storage: Graph edges via `session.RelateAsync()`, queried via `IGraphQuery<T>`  
Registration: `AddDaliStores<IdentityUser, IdentityRole, string>()`

## Events & Triggers

Dali has built-in `EventTriggerOptions` via `StoreOptions.Events.Triggers`. SurrealDB `DEFINE EVENT` triggers run inside the same transaction and see `$before`/`$after` snapshots.

Currently not used for cascade operations (role edges auto-cascade via `RELATE`). Could be added later for auditing or cross-table consistency if needed.

See `src/Dali/Events/EventTriggerDefinition.cs` and `src/Dali/Events/TriggerActionBuilder.cs` for the fluent trigger API.

## Build & Publishing

- `"$RepoRoot/src/Dali.AspNetIdentity"` is in the `$libProjects` array in `build/nuget-pack.ps1`
- Package metadata inherited from `src/Directory.Build.props`

## Tests

**Location**: `tests/Dali.AspNetIdentity.Tests/`

**Framework**: TUnit + NSubstitute + Shouldly + Bogus

**Test coverage**: 125 unit tests across 7 files covering all 15 user store interfaces and 2 role store interfaces.

| File | Tests | Coverage |
|------|-------|----------|
| `DaliRoleStoreTests.cs` | 19 | Role CRUD + queryable |
| `DaliUserStoreCoreTests.cs` | 35 | Core CRUD, password, email, phone, 2FA, stamp, lockout |
| `DaliUserStoreClaimTests.cs` | 8 | Full claims |
| `DaliUserStoreLoginTests.cs` | 6 | External logins |
| `DaliUserStoreRoleTests.cs` | 12 | Role membership (graph edges) |
| `DaliUserStoreAuthTests.cs` | 12 | Authenticator key, recovery codes, tokens |
| `DaliUserStorePasskeyTests.cs` | 9 | Passkey CRUD |

All stores mocked via NSubstitute with `IDocumentStore`, `IQuerySession`, `IDocumentSession`, `ISableQueryable<T>`, and `IGraphQuery<T>`.
