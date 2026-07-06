---
title: Quick Start
description: Get AeroDB running in under 5 minutes
---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- [SurrealDB](https://surrealdb.com/docs/installation) 3.1 or later — running locally, on a remote host, or via [SurrealDB Cloud](https://surrealdb.com/cloud)
- A SurrealDB namespace and database (can be created on-the-fly)

## Installation

Add the AeroDB package to your project:

```bash
dotnet add package AeroDB
```

AeroDB also ships optional packages for ASP.NET Core Identity and Wolverine integration:

```bash
# ASP.NET Core Identity support
dotnet add package AeroDB.AspNetIdentity

# Wolverine messaging integration
dotnet add package AeroDB.WolverineFx
```

## Basic Setup

Create a document store by configuring the connection and schema options. The store manages your SurrealDB connection, document mappings, indexes, and schema lifecycle.

```csharp
using AeroDB;

var store = Documents.For(opts =>
{
    opts.Connection("http://localhost:8000", "myapp", "production");
});
```

If you prefer setting properties individually:

```csharp
var store = Documents.For(opts =>
{
    opts.Endpoint  = "http://localhost:8000";
    opts.Namespace = "myapp";
    opts.Database  = "production";
});
```

### Initialize the Store

Call `InitializeAsync` once at application startup. This connects to SurrealDB, creates the namespace/database, applies document schemas, and sets up indexes.

```csharp
await store.InitializeAsync();
```

> **Tip**: `InitializeAsync` is idempotent — calling it multiple times is safe. The store will only fully initialize once.

## Define a Document

AeroDB works with plain C# objects (POCOs). For SurrealDB-native table types, inherit from `Record` (from the `SurrealDb.Net.Models` namespace). For lightweight use, any class works.

```csharp
public class User
{
    // SurrealDB assigns a RecordId as the primary key.
    // You can provide an explicit identity mapping in schema config.
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
```

## First Write

Open a lightweight session, store a document, and save changes.

```csharp
await using var session = store.LightweightSession();

session.Store(new User
{
    Name  = "Alice",
    Email = "alice@example.com"
});

await session.SaveChangesAsync();
```

The `LightweightSession` (tracking mode: `DocumentTracking.None`) is the fastest session — it does not track changes, so every `Store` call queues an insert/update.

## First Read

Open a query session and use LINQ to retrieve documents.

```csharp
await using var query = store.QuerySession();

var alice = await query.Query<User>()
    .FirstOrDefaultAsync(u => u.Email == "alice@example.com");
```

AeroDB translates LINQ expressions to SurrealQL queries, so you get compile-time safety with full database execution.

### Load by RecordId

If you know the SurrealDB record ID:

```csharp
var recordId = new RecordId("user", "abc123");
var user = await query.LoadAsync<User>(recordId);
```

## Update a Document

Load, modify, and save:

```csharp
await using var session = store.LightweightSession();

var alice = session.Query<User>()
    .First(u => u.Email == "alice@example.com");

alice.Name = "Alice Smith";
session.Store(alice);
await session.SaveChangesAsync();
```

## Delete a Document

```csharp
await using var session = store.LightweightSession();

var alice = session.Query<User>()
    .First(u => u.Email == "alice@example.com");

session.Delete(alice);
await session.SaveChangesAsync();
```

## Schema & Indexes

For production applications, you'll want to configure document schemas — defining identities, unique constraints, full-text search, and vector indexes. All of this is done fluently during store configuration.

```csharp
var store = Documents.For(opts =>
{
    opts.Connection("http://localhost:8000", "myapp", "production");

    opts.Schema.For<User>()
        .Identity(u => u.Id)
        .Index(u => new { u.FirstName, u.LastName })
        .FullTextIndex(u => u.Bio, "english");
});
```

When `InitializeAsync` runs, AeroDB automatically calls `DEFINE TABLE` and `DEFINE INDEX` for each configured mapping.

## ASP.NET Core Integration

Register AeroDB in your DI container with `AddAeroDB`:

```csharp
builder.Services.AddAeroDB(opts =>
{
    opts.Connection("http://localhost:8000", "myapp", "production");
});
```

The store is registered as a singleton and available via `IDocumentStore` in your controllers and minimal API handlers.

```csharp
app.MapPost("/users", async (User user, IDocumentStore store) =>
{
    await using var session = store.LightweightSession();
    session.Store(user);
    await session.SaveChangesAsync();
    return Results.Created($"/users/{user.Id}", user);
});
```

### ASP.NET Core Identity

To use AeroDB as the backing store for ASP.NET Core Identity, add the Identity package and chain `.AddAeroDBStores<TUser, TRole>()` onto the Identity builder:

```bash
dotnet add package AeroDB.AspNetIdentity
```

```csharp
// Register AeroDB document store
builder.Services.AddAeroDB(o =>
{
    o.Namespace = "aero";
    o.Database   = "identity";
    o.ClientFactory = () => new SurrealDbClient("ws://localhost:8000/rpc");
});

// Add ASP.NET Core Identity backed by SurrealDB
builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    options.SignIn.RequireConfirmedAccount = true)
    .AddRoles<IdentityRole>()
    .AddAeroDBStores<IdentityUser, IdentityRole>();
```

The `AddAeroDBStores` call auto-configures SurrealDB schema for your user and role types — it sets identity columns, unique indexes on `NormalizedUserName` and `NormalizedEmail`, plus custom fields for authenticator keys, recovery codes, and role IDs. No manual `.Schema.For<T>()` needed for Identity entities.

**Passkey support** (WebAuthn/FIDO2):

```csharp
builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    options.UserVerificationRequirement = "preferred";
    options.ResidentKeyRequirement = "preferred";
});
```

AeroDB handles user and role storage, claims, tokens, logins, and passkeys through SurrealDB — no EF Core or SQL database required.

## Next Steps

- **[Configuration](/docs/getting-started/configuration)** — deep dive into all connection, schema, and store options
- **[Installation](/docs/getting-started/installation)** — package reference and version requirements
- **[Document Schema](/docs/schema/overview)** — identity, indexes, analyzers, and schema modes
- **[Multi-Tenancy](/docs/multi-tenancy/overview)** — conjoined and database-per-tenant strategies
- **[Event Sourcing](/docs/events/overview)** — event store, projections, and subscriptions
- **[LINQ Querying](/docs/querying/linq)** — full LINQ provider reference
- **[Examples](/docs/examples)** — end-to-end application examples
