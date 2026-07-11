---
title: Configuration
description: Connect and configure AeroDB for SurrealDB
---

AeroDB connects to SurrealDB via a simple fluent builder. You can target local, remote, embedded, or cloud instances.

```csharp
using AeroDB;

// Local SurrealDB instance
var store = DocumentStore.For(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Database("myapp");
    cfg.Namespace("production");
    cfg.AutoCreateSchemaObjects = AutoCreate.All;
});

// In-memory (for testing)
var store = DocumentStore.For(cfg =>
{
    cfg.Connection("memory");
    cfg.Database("test");
});

// ASP.NET Core DI registration
builder.Services.AddAeroDB(cfg =>
{
    cfg.Connection(builder.Configuration.GetConnectionString("SurrealDB")!);
});
```

Open a session and start working:

```csharp
await using var session = store.QuerySession();
var users = await session.Query<User>().Where(u => u.Active).ToListAsync();
```

**Full sample:** [`samples/MinimalAPI/`](https://github.com/microbian-systems/AeroDB/tree/main/samples/MinimalAPI)
