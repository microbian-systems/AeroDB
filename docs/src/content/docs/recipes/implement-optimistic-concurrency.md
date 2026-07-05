---
title: Implement Optimistic Concurrency
description: Detect and handle concurrent write conflicts
---

## Goal

Prevent lost updates by using version-based optimistic concurrency.

## Code

```csharp
using AeroDB;

// 1. Define your entity with a version field
public sealed record User : IVersioned
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public Guid Version { get; init; } = Guid.NewGuid();
}

// 2. Configure the store
var store = DocumentStore.For(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Database("myapp");
    cfg.Namespace("production");
    cfg.UseOptimisticConcurrency = true;
});

// 3. Read a document
await using var session = store.LightweightSession();
var user = await session.LoadAsync<User>("user:1");

// 4. Modify and save
user = user with { Email = "new-email@example.com" };
session.Store(user);

try
{
    await session.SaveChangesAsync();
}
catch (ConcurrencyException ex)
{
    Console.WriteLine($"Conflict detected. Reload and retry: {ex.Message}");
    // Reload, reapply your change, and try again
}
```

## Explanation

- `IVersioned` adds a `Version` property that AeroDB checks on every write.
- When `UseOptimisticConcurrency = true`, `SaveChangesAsync()` throws `ConcurrencyException` if another session modified the document since it was loaded.
- Use immutable records (`record` + `with`) for clean update semantics.
- Always handle `ConcurrencyException` — reload the document, reapply the change, and retry.

## Retry Pattern

```csharp
async Task UpdateEmailAsync(string userId, string newEmail)
{
    for (var retry = 0; retry < 3; retry++)
    {
        await using var session = store.LightweightSession();
        var user = await session.LoadAsync<User>(userId);
        user = user with { Email = newEmail };
        session.Store(user);

        try { await session.SaveChangesAsync(); return; }
        catch (ConcurrencyException) when (retry < 2) { }
    }
    throw new InvalidOperationException("Failed after 3 retries");
}
```

## See Also

- [API: ConcurrencyException](/api/AeroDB.ConcurrencyException)
- [API: IVersioned](/api/AeroDB.IVersioned)
