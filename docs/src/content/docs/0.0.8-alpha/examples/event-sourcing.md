---
title: Event Sourcing
description: Append-only events, projections, and async daemon
slug: 0.0.8-alpha/examples/event-sourcing
---

AeroDB supports full event sourcing with append-only event streams, streaming projections, and an async daemon for background processing.

## Appending Events

```csharp
// Start an event stream
var streamId = Guid.NewGuid();
session.Events.StartStream<QuestParty>(streamId,
    new QuestStarted(streamId, "The Fellowship"),
    new MembersJoined(streamId, 1, "Rivendell", ["Frodo", "Sam"]));
await session.SaveChangesAsync();

// Append more events
await session.Events.Append(streamId,
    new MembersJoined(streamId, 2, "Moria", ["Gandalf", "Legolas"]));
await session.SaveChangesAsync();
```

## Projections

```csharp
// Single-stream projection that builds a read model
public sealed class QuestProjection : SingleStreamProjection<Quest, Guid>
{
    public static Quest Create(QuestStarted started) =>
        new(started.QuestId, [], [], started.Name, false);

    public static Quest Apply(MembersJoined joined, Quest quest) =>
        quest with { Members = quest.Members.Union(joined.Members).ToList() };
}

// Register in configuration
storeOptions.Projections.Add<QuestProjection>(ProjectionLifecycle.Inline);
```

## Async Daemon

```csharp
// Start the async daemon in your host
builder.Services.AddAeroDB(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Projections.Daemon.Mode = DaemonMode.HotCold;
});

// The daemon processes events in the background,
// continuously updating projections
```

**Full sample:** [`samples/EventSourcingIntro/`](https://github.com/microbians/AeroDB/tree/main/samples/EventSourcingIntro)
