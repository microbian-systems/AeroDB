---
title: Events
description: Event sourcing in AeroDB
---

AeroDB provides a full **event sourcing** subsystem built on SurrealDB's `mt_events` table. Events are stored as immutable records and replayed to reconstruct aggregate state.

## Enabling Events

Events must be explicitly enabled:

```csharp
var store = Documents.For(opts =>
{
    opts.Connection("http://localhost:8000", "myns", "mydb");
    opts.Events.Enabled = true;
});
```

## Event Streams and Aggregates

An **event stream** is an ordered sequence of events identified by a stream ID (string or Guid). Each event carries a version number, a global sequence, a timestamp, and the event data.

The `IEvent<T>` envelope wraps domain events with metadata:

```csharp
public interface IEvent<out T>
{
    T Data { get; }
    long Version { get; }
    long Sequence { get; }
    DateTimeOffset Timestamp { get; }
    string StreamId { get; }
    Guid StreamKey { get; }
}
```

## Appending Events

Events are appended to a stream through `IEvents.Append()`:

```csharp
var events = await session.Events.Append("order-123", new object[]
{
    new OrderCreated { OrderId = "order-123", CustomerId = "cust-456" },
    new OrderItemAdded { ProductId = "prod-789", Quantity = 2 }
});
```

For optimistic concurrency, provide the expected stream version:

```csharp
await session.Events.Append("order-123", expectedVersion: 5, newEvents);
```

## Reading Aggregate State

Reconstruct aggregate state by replaying events:

```csharp
// Full stream aggregation
var order = await session.Events.AggregateStreamAsync<Order>("order-123");

// With a pre-existing state object
var state = await session.Events.AggregateStreamAsync("order-123", state: myExistingState);
```

The aggregate must define `Apply` or `When` methods that receive each event type:

```csharp
public class Order
{
    public string Id { get; set; }
    public string CustomerId { get; set; }
    public List<string> Items { get; set; } = new();

    public void Apply(OrderCreated e) { Id = e.OrderId; CustomerId = e.CustomerId; }
    public void Apply(OrderItemAdded e) { Items.Add(e.ProductId); }
}
```

## Projections

Projections create derived read models from event streams. See the [Projections](./projections) page for details.

## Change Log / Audit Trails

Every event stores metadata including correlation ID, causation ID, timestamp, and custom headers. This creates an immutable audit trail for compliance and debugging. Use `FetchAllAfterSequence()` to build change-data-capture pipelines.

## Stream Management

```csharp
// Stream metadata
var state = await session.Events.FetchStreamStateAsync("order-123");
// → Version, Created, LastModified, IsArchived

// Archive a stream (moves events to archive table)
await session.Events.ArchiveStream("order-123");

// Compact a stream (replace all events with a snapshot)
await session.Events.CompactStreamAsync<Order>("order-123");

// Bulk insert events
await session.Events.BulkInsertEventsAsync([("stream-1", events1), ("stream-2", events2)]);
```
