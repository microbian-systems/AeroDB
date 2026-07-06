---
title: Projections
description: Event projections in AeroDB
---

Projections create **derived read models** from event streams. Instead of querying raw events and aggregating every time, projections maintain denormalized views that are updated as events are appended.

## Projection Lifecycles

AeroDB supports two projection lifecycles:

- **Inline** — Projections execute during `SaveChangesAsync()`, in the same SurrealDB transaction as the event append. The projected document is written immediately.
- **Async** — Projections run in a background `AsyncDaemon` process. They poll for new events and update documents asynchronously, offering better write-throughput at the cost of eventual consistency.

## Inline Projections

### Single-Stream Projections

`SingleStreamProjection<T>` creates one projected document per event stream. The document ID is derived from the stream ID:

```csharp
public class OrderProjection : SingleStreamProjection<OrderSummary>
{
    public OrderSummary Create(OrderCreated e) =>
        new() { Id = e.OrderId, CustomerId = e.CustomerId, Status = "new" };

    public void Apply(OrderShipped e, OrderSummary view) =>
        view.Status = "shipped";
}
```

### Event Projections

`EventProjection<T>` uses source-generator-dispatched per-event-type methods for `Create`, `Apply`, and `ShouldDelete`:

```csharp
public partial class OrderListProjection : EventProjection<OrderListItem>
{
    public OrderListItem Create(OrderCreated e, IEvent<OrderCreated> _) =>
        new() { Id = e.OrderId, CustomerName = e.CustomerName };

    public void Apply(OrderShipped e, OrderListItem item, IEvent<OrderShipped> _) =>
        item.Shipped = true;

    public bool ShouldDelete(OrderCancelled e, OrderListItem item, IEvent<OrderCancelled> _) =>
        true;
}
```

### Multi-Stream Projections

`MultiStreamProjection<T>` aggregates events from multiple streams into a single projected document. Use `Identity<TEvent>()` to declare which event property identifies the target document:

```csharp
public class CustomerDashboard : MultiStreamProjection<CustomerDashboard, string>
{
    public CustomerDashboard()
    {
        Identity<OrderCreated>(e => e.CustomerId);
        Identity<PaymentReceived>(e => e.CustomerId);
    }

    public void Apply(OrderCreated e, CustomerDashboard dash) => dash.TotalOrders++;
    public void Apply(PaymentReceived e, CustomerDashboard dash) => dash.TotalPaid += e.Amount;
}
```

## Async Projections

Async projections are configured identically but registered separately. They run in the `AsyncDaemon` background service, which tracks progress via the `mt_projection_progress` table and processes events in order.

```csharp
opts.Projections.Add(new OrderProjection(), ProjectionLifecycle.Async);
```

The daemon processes projections when started:

```csharp
var daemon = new AsyncDaemon(store);
await daemon.StartAllAsync();
```

## Rebuilding Projections

Projections can be rebuilt from scratch — all events are replayed to reconstruct the projected document set:

```csharp
// On startup
opts.ProjectionBuild.RebuildOnStartup = true;

// Or per-projection
await projection.RebuildAsync(session, ct);
```

## Use Cases

- **CQRS read-side** — Maintain optimized query tables that differ from your write model
- **Denormalization** — Flatten relationships into a single document for fast reads
- **Snapshots** — Store pre-aggregated state to avoid replaying the full event stream
- **Reporting** — Build materialized views for dashboards and analytics
