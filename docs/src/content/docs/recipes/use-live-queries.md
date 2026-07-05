---
title: Use Live Queries
description: Subscribe to real-time data changes with reactive streams
---

## Goal

Subscribe to changes on user documents and react in real time.

## Code

```csharp
using AeroDB;
using AeroDB.Reactive;

var store = DocumentStore.For(cfg =>
    cfg.Connection("ws://localhost:8000/rpc"));

await using var session = store.LiveQuerySession();

// ── Subscribe to all changes ─────────────────────────────

var sub = session.LiveQuery<User>()
    .Where(u => u.Active)
    .Subscribe(change =>
    {
        switch (change.Action)
        {
            case AeroDBLiveAction.Create:
                Console.WriteLine($"New user: {change.Result.Name}");
                break;
            case AeroDBLiveAction.Update:
                Console.WriteLine($"Updated: {change.Result.Name} → {change.Result.Email}");
                break;
            case AeroDBLiveAction.Delete:
                Console.WriteLine($"Deleted: {change.Result.Name}");
                break;
        }
    });

// ── Filter by change type ────────────────────────────────

var newUsers = session.LiveQuery<User>()
    .SelectOnCreate()
    .Subscribe(user => OnboardNewUser(user));

// ── In ASP.NET Core ──────────────────────────────────────

app.MapGet("/users/stream", async (HttpContext ctx, IDocumentStore store) =>
{
    ctx.Response.ContentType = "text/event-stream";
    await using var session = store.LiveQuerySession();
    var cts = ctx.RequestAborted;

    session.LiveQuery<User>()
        .Where(u => u.Active)
        .Subscribe(async change =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(change);
            await ctx.Response.WriteAsync($"data: {json}\n\n", cts);
            await ctx.Response.Body.FlushAsync(cts);
        }, cts);

    await Task.Delay(Timeout.Infinite, cts).ContinueWith(_ => { });
});

// ── Cleanup ──────────────────────────────────────────────

sub.Dispose();
```

## Explanation

- `LiveQuerySession()` opens a session capable of real-time subscriptions.
- `LiveQuery<T>()` returns an `IObservable<AeroDBLiveChange<T>>` stream.
- `Subscribe()` receives `AeroDBLiveChange<T>` with `Action` (Create/Update/Delete) and `Result`.
- `SelectOnCreate()`, `SelectOnUpdate()`, `SelectOnDelete()` filter the stream to specific change types.
- For SSE endpoints, serialize each change and flush to the HTTP response.
- Always `Dispose()` subscriptions when done.

## See Also

- [Live Queries](/guides/live-queries/)
- [Example: Live Queries](/examples/live-queries/)
- [API: ILiveQuerySession](/api/AeroDB.LiveQuery.ILiveQuerySession)
