---
title: Event Sourcing
description: Event sourcing with AeroDB
---

AeroDB's event sourcing module provides append-only event streams, aggregate reconstruction, and projection support on top of SurrealDB.

## Setting Up Event Store

Define your events and aggregate:

```csharp
public record UserRegistered(string Email, string Name);
public record UserEmailChanged(string NewEmail);

public class UserAggregate
{
    public string Id { get; set; }
    public string Email { get; set; }
    public string Name { get; set; }
    public int Version { get; set; }
}
```

Register the event store in DI:

```csharp
services.AddAeroDbEventSourcing(cfg =>
{
    cfg.AddEvent<UserRegistered>();
    cfg.AddEvent<UserEmailChanged>();
    cfg.StreamPrefix = "user";
});
```

## Starting and Appending to Streams

```csharp
var stream = await session.EventStore.StartStreamAsync<UserAggregate>(
    "user:alice",
    new UserRegistered("alice@example.com", "Alice"));

await session.SaveChangesAsync();
```

Append new events:

```csharp
var stream = await session.EventStore.LoadStreamAsync("user:alice");
stream.Append(new UserEmailChanged("alice@newdomain.com"));
await session.SaveChangesAsync();
```

## Reading Aggregate State

Reconstruct the current state by folding events:

```csharp
var aggregate = await session.EventStore.AggregateAsync<UserAggregate>("user:alice");
// aggregate.Email == "alice@newdomain.com"
```

Read event history:

```csharp
var events = await session.EventStore.ReadEventsAsync("user:alice",
    fromVersion: 0, toVersion: 10);
```

## Inline Projections

Projections that run synchronously within the append transaction:

```csharp
public class UserEmailProjection : IInlineProjection
{
    public void Apply(IEventStoreSession session, IEvent @event)
    {
        if (@event is UserEmailChanged e)
        {
            session.Store(new UserEmailChange
            {
                UserId = @event.StreamId,
                OldEmail = e.PreviousEmail,
                NewEmail = e.NewEmail,
                ChangedAt = @event.Timestamp
            });
        }
    }
}
```

## Async Projections with Daemon

Long-running projections using the projection daemon:

```csharp
public class UserSummaryProjection : IAsyncProjection
{
    public string Name => "user-summary";
    public async Task ProjectAsync(IReadOnlyList<IEvent> batch, ISession session)
    {
        foreach (var @event in batch.OfType<UserRegistered>())
        {
            var summary = await session.LoadAsync<UserSummary>(@event.StreamId)
                         ?? new UserSummary();
            summary.TotalUsers++;
            session.Store(summary);
        }
        await session.SaveChangesAsync();
    }
}
```

Register and run:

```csharp
services.AddAeroDbProjection<UserSummaryProjection>(cfg =>
{
    cfg.BatchSize = 100;
    cfg.Interval = TimeSpan.FromSeconds(5);
});
```

## Change Log Consumption

Subscribe to the global event log for cross-service integration:

```csharp
var subscription = session.EventStore.Subscribe("user.*", async (streamId, @event) =>
{
    Console.WriteLine($"Stream {streamId} produced {@event.GetType().Name}");
    // Forward to message bus, cache, etc.
});
```

## Stream Versioning

Concurrency control with expected versions:

```csharp
// Optimistic — fail if stream has new events
stream.Append(new UserEmailChanged("alice@new.com"), expectedVersion: 3);

// Read current version
var version = stream.Version;

// Check for conflicts
if (await session.EventStore.StreamExistsAsync("user:alice", minVersion: 3))
{
    // Handle conflict: reload, merge, retry
}
```

Use `NoStream` / `AnyVersion` sentinels for initial creates or blind appends.
