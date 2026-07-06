---
title: Live Queries
description: Real-time reactive queries
---

Live queries let you subscribe to real-time changes on SurrealDB tables using `IObservable<T>`, enabling reactive UIs, cache invalidation, and dashboard streaming.

## Subscribing to Live Table Changes

```csharp
var subscription = session.LiveQuery<User>()
    .Subscribe(change =>
    {
        Console.WriteLine($"User {change.Result.Id} was {change.Action}");
    });
```

The `change` object contains:
- `Action` — `Create`, `Update`, or `Delete`
- `Result` — the affected entity
- `Id` — the record ID that changed

## IObservable\<T\> Integration

Live query subscriptions implement `IObservable<T>`, so you can use Rx.NET:

```csharp
var subscription = session.LiveQuery<Order>()
    .Where(o => o.Status == "pending")
    .Subscribe(onNext: order =>
    {
        UpdateDashboard(order);
    }, onError: ex =>
    {
        _logger.LogError(ex, "Live query failed");
    });
```

## Filtering Live Queries

Apply LINQ filters server-side so only matching changes arrive:

```csharp
var subscription = session.LiveQuery<SensorReading>()
    .Where(s => s.Temperature > 100.0)
    .Subscribe(reading =>
    {
        TriggerAlert(reading);
    });
```

Filters are pushed to SurrealDB — the client only receives matching events.

## Action Filtering

Subscribe to only specific change types:

```csharp
var creates = session.LiveQuery<User>()
    .Where(u => u.Department == "Engineering")
    .OnCreate()
    .Subscribe(user => WelcomeNewHire(user));

var updates = session.LiveQuery<User>()
    .OnUpdate()
    .Subscribe(user => InvalidateCache(user.Id));

var deletes = session.LiveQuery<User>()
    .OnDelete()
    .Subscribe(user => AuditLog("User deleted", user.Id));
```

## Rx.NET Composition with ToObservable()

Pipe live queries through Rx operators for debouncing, throttling, and aggregation:

```csharp
var observable = session.LiveQuery<StockPrice>()
    .ToObservable()
    .Where(c => c.Action == ChangeAction.Update)
    .Throttle(TimeSpan.FromMilliseconds(200))
    .Select(c => c.Result)
    .GroupBy(s => s.Symbol)
    .SelectMany(group => group.Take(1));

var disposable = observable.Subscribe(price =>
    UpdateChart(price.Symbol, price.Last));
```

## Aggregate State from Live Streams

Maintain an in-memory aggregate that stays current:

```csharp
var state = new Dictionary<string, User>();
var subscription = session.LiveQuery<User>()
    .Subscribe(change =>
    {
        switch (change.Action)
        {
            case ChangeAction.Create:
                state[change.Result.Id] = change.Result;
                break;
            case ChangeAction.Update:
                state[change.Result.Id] = change.Result;
                break;
            case ChangeAction.Delete:
                state.Remove(change.Result.Id);
                break;
        }
        OnStateChanged();
    });
```

## Unsubscribing and Cleanup

`Subscribe` returns `IDisposable`. Dispose to stop the live query:

```csharp
var disposable = session.LiveQuery<User>().Subscribe(OnUserChange);

// Later — cleanup
disposable.Dispose();
```

For scoped subscriptions tied to a component lifecycle:

```csharp
public class UserMonitor : IAsyncDisposable
{
    private readonly IDisposable _subscription;

    public UserMonitor(ISession session)
    {
        _subscription = session.LiveQuery<User>()
            .Where(u => u.IsOnline)
            .Subscribe(OnUserOnline);
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
    }
}
```

## Use Cases

- **Reactive UI** — Blazor components that re-render on database changes without polling
- **Cache invalidation** — Evict cached query results when the underlying table changes
- **Real-time dashboards** — Live metrics that update as new data arrives
- **Audit monitoring** — Stream sensitive table changes to an audit log
- **Cross-service sync** — Keep in-memory caches synchronized across application instances by subscribing to shared tables

Live queries use SurrealDB's built-in live query protocol over WebSockets, so they remain efficient even with many concurrent subscriptions.
