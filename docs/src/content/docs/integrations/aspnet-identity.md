---
title: ASP.NET Identity
description: Use AeroDB as an ASP.NET Core Identity store
---

# ASP.NET Identity

AeroDB provides a complete implementation of ASP.NET Core Identity stores backed by SurrealDB, replacing the default Entity Framework Core persistence layer without sacrificing any Identity features.

## Overview

The `AeroDB.AspNetCore.Identity` package implements `IUserStore`, `IRoleStore`, and all optional Identity store interfaces (`IUserPasswordStore`, `IUserEmailStore`, `IUserClaimStore`, `IUserLoginStore`, `IUserTokenStore`, `IRoleClaimStore`, etc.) using SurrealDB tables and records.

## Installation

```shell
dotnet add package AeroDB.AspNetCore.Identity
```

## Registration

Replace the default EF Core identity registration with the AeroDB identity store:

```csharp
// Program.cs
builder.Services
    .AddIdentity<AppUser, AppRole>()
    .AddAeroDbStores(options =>
    {
        // The SurrealDB namespace and database used for identity tables
        options.Namespace = "aero";
        options.Database = "identity";
        
        // Optional: customize table names
        options.UsersTable = "identity_user";
        options.RolesTable = "identity_role";
        options.UserClaimsTable = "identity_user_claim";
        options.UserLoginsTable = "identity_user_login";
        options.UserTokensTable = "identity_user_token";
        options.RoleClaimsTable = "identity_role_claim";
    });
```

The identity stores use the same SurrealDB connection already configured via `AddAeroDB()`. No additional connection management is required.

## User and Role Storage

Users and roles are stored as SurrealDB records with a deterministic record ID scheme:

```csharp
public class AppUser : IdentityUser<long>
{
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
}

public class AppRole : IdentityRole<long>
{
    public string? Description { get; set; }
}
```

Record IDs follow the pattern `{table}:{id}` (e.g., `identity_user:12345`). The numeric `Id` uses Snowflake IDs by default, configurable via `IdentityOptions.SnowflakeGenerator`.

## Claims, Tokens, and Logins

Each optional store persists into its own SurrealDB table, indexed for efficient lookups:

- **Claims** — `identity_user_claim` with composite index on `UserId + ClaimType`
- **Logins** — `identity_user_login` keyed by `LoginProvider + ProviderKey`
- **Tokens** — `identity_user_token` with composite key on `UserId + LoginProvider + Name`
- **Role Claims** — `identity_role_claim` indexed by `RoleId`

All store implementations support batch operations and use SurrealDB transactions for consistency.

## Migrating from EF Core Identity

1. **Export existing data** from your legacy identity tables to a script of SurrealQL `CREATE` statements.
2. **Run the migration script** against your SurrealDB instance using the SurrealDB CLI or REST API.
3. **Update `Program.cs`** to replace `AddEntityFrameworkStores<TContext>()` with `AddAeroDbStores()`.
4. **Verify** by signing in a known user and confirming claims/tokens resolve correctly.

A migration helper is available:

```csharp
// Bulk-import users from an IQueryable<IdentityUser> source
await options.MigrateFromAsync(sourceUsers, sourceRoles, cancellationToken);
```

## User Options

Standard ASP.NET Core Identity options like `Password`, `Lockout`, `SignIn`, and `User` configuration work identically to EF Core — they are store-independent:

```csharp
builder.Services.Configure<IdentityOptions>(options =>
{
    options.Password.RequiredLength = 8;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.User.RequireUniqueEmail = true;
});
```

## See Also

- [Getting Started](/docs/getting-started)
- [Configuration](/docs/configuration)
