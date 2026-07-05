---
title: Register in DI
description: Add AeroDB to your ASP.NET Core dependency injection container
---

## Goal

Register AeroDB in ASP.NET Core DI so you can inject `IDocumentStore`, `IQuerySession`, or `IDocumentSession` into controllers and minimal APIs.

## Code

```csharp
// Program.cs
using AeroDB;
using AeroDB.AspNetIdentity;

var builder = WebApplication.CreateBuilder(args);

// ── AeroDB ────────────────────────────────────────────────

builder.Services.AddAeroDB(cfg =>
{
    cfg.Connection(builder.Configuration.GetConnectionString("SurrealDB")!);
    cfg.AutoCreateSchemaObjects = AutoCreate.All;
});

// ── With Wolverine ────────────────────────────────────────

builder.Host.UseWolverine(opts =>
{
    opts.Services.AddAeroDB(cfg =>
        cfg.Connection(builder.Configuration.GetConnectionString("SurrealDB")!));
});

// ── With ASP.NET Identity ────────────────────────────────

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>()
    .AddAeroDBStores();

var app = builder.Build();

// ── Inject in minimal APIs ────────────────────────────────

app.MapGet("/users/{id}", async (string id, IQuerySession session) =>
{
    return await session.LoadAsync<User>(id);
});

app.MapPost("/users", async (User user, IDocumentSession session) =>
{
    session.Store(user);
    await session.SaveChangesAsync();
    return Results.Created($"/users/{user.Id}", user);
});

app.Run();
```

## Explanation

- `AddAeroDB(cfg => ...)` registers `IDocumentStore` as a singleton, `IQuerySession` and `IDocumentSession` as scoped services.
- `AddAeroDBStores()` wires up ASP.NET Core Identity to use AeroDB for user/role storage.
- Wolverine integration requires `WolverineOptionsAeroDBExtensions` from the Wolverine host builder.
- `IQuerySession` is read-only — inject for GET endpoints.
- `IDocumentSession` is read-write — inject for POST/PUT/DELETE endpoints.

## See Also

- [Configuration](/getting-started/configuration/)
- [Integrations: ASP.NET Identity](/integrations/aspnet-identity/)
