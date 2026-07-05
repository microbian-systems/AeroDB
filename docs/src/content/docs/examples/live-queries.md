---
title: Live Queries
description: Real-time reactive subscriptions via Rx.NET
---

SurrealDB supports live queries that push changes to clients in real time. AeroDB wraps these in Rx.NET `IObservable<T>` streams.

```csharp
using AeroDB.Reactive;

// Subscribe to live changes on a query
var subscription = session.LiveQuery<User>()
    .Where(u => u.Active)
    .Subscribe(change =>
    {
        switch (change.Action)
        {
            case AeroDBLiveAction.Create:
                Console.WriteLine($"User created: {change.Result.Name}");
                break;
            case AeroDBLiveAction.Update:
                Console.WriteLine($"User updated: {change.Result.Name}");
                break;
            case AeroDBLiveAction.Delete:
                Console.WriteLine($"User deleted: {change.Result.Name}");
                break;
        }
    });

// Filter for specific change types only
session.LiveQuery<User>()
    .SelectOnCreate()
    .Subscribe(user => Console.WriteLine($"New signup: {user.Name}"));

session.LiveQuery<User>()
    .SelectOnUpdate()
    .Subscribe(user => Console.WriteLine($"Profile updated: {user.Name}"));

// Dispose when done
subscription.Dispose();
```

**Full sample:** [`samples/`](https://github.com/microbians/AeroDB/tree/main/samples)
