---
title: Reactive Streams
description: Bridge live queries into Rx.NET IObservable<T> streams for declarative event processing
---

Bridge live queries into Rx.NET `IObservable<T>` streams for declarative event processing. Filter by action type (created/updated/deleted), accumulate state over time, or compose with standard Rx operators — all without polling.

## Installation

```bash
dotnet add package AeroDB.Reactive
```

## Action Shortcuts

Filter by action type directly on the builder — no need to pipe through Rx manually:

```csharp
var created = session.LiveQuery<User>()
    .OnCreate()
    .Subscribe(user => Console.WriteLine($"New user: {user.Name}"));

var updated = session.LiveQuery<User>()
    .OnUpdate()
    .Subscribe(user => Console.WriteLine($"Updated: {user.Name}"));

var deleted = session.LiveQuery<User>()
    .OnDelete()
    .Subscribe(id => Console.WriteLine($"Deleted: {id}"));
```

## Rx.NET Composition

For custom filtering, transformation, or combining multiple streams, pipe through `ToObservable()` and use standard Rx operators:

```csharp
// Filter by action type with LINQ operators
var creations = session.LiveQuery<User>()
    .ToObservable()
    .SelectOnCreate()
    .Subscribe(user => Console.WriteLine($"New user: {user.Name}"));

// Combine multiple streams
var users = session.LiveQuery<User>().ToObservable().SelectOnCreate();
var orders = session.LiveQuery<Order>().ToObservable().SelectOnCreate();

users.Merge(orders)
    .Subscribe(created => Console.WriteLine($"Created: {created}"));
```

## State Accumulation

Accumulate state from a live stream without managing state manually:

```csharp
var state = await session.LiveQuery<Order>()
    .ToObservable()
    .AggregateRecords(new Dictionary<string, Order>());
```

## Available Operators

| Operator | Signature | Description |
|----------|-----------|-------------|
| `OnCreate<T>()` | `IObservable<T>` | Shortcut: subscribe only to CREATE events |
| `OnUpdate<T>()` | `IObservable<T>` | Shortcut: subscribe only to UPDATE events |
| `OnDelete<T>()` | `IObservable<T>` | Shortcut: subscribe only to DELETE events |
| `ToObservable<T>()` | `IObservable<AeroDBLiveChange<T>>` | Bridge the live query builder into an Rx observable |
| `SelectOnCreate<T>()` | `IObservable<T>` | Filter + project CREATE events to the entity |
| `SelectOnUpdate<T>()` | `IObservable<T>` | Filter + project UPDATE events to the entity |
| `SelectOnDelete<T>()` | `IObservable<T>` | Filter + project DELETE events to the entity |
| `SelectResults<T>()` | `IObservable<T>` | Project results from all change types |
| `AggregateRecords<T>()` | `IObservable<IDictionary<string, T>>` | Accumulate entities into a dictionary keyed by ID |

## See Also

- [Live Queries](/guides/live-queries) — basic live query setup
- [Live Query Examples](/examples/live-queries) — real-world patterns
