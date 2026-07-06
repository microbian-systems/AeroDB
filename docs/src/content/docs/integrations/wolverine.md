---
title: Wolverine
description: Message persistence and transport via Wolverine
---

# Wolverine

AeroDB integrates with [WolverineFx](https://wolverinefx.net) using SurrealDB as the message durability store — providing outbox, saga, and scheduled message capabilities without requiring a separate database.

## Installation

```shell
dotnet add package AeroDB.Wolverine
```

This package registers SurrealDB-backed implementations of Wolverine's `IMessageStore`, `INodeStatePersistence`, and saga repositories.

## Message Durability

Messages are persisted as SurrealDB records and benefit from SurrealDB's built-in replication and durability guarantees:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithAeroDb(s =>
        {
            s.ConnectionString = "ws://localhost:8000";
            s.Namespace = "aero";
            s.Database = "wolverine";
        });

        // All Wolverine endpoints now survive restarts
        opts.PublishAllMessages().ToAeroDbQueue("default");
    })
    .StartAsync();
```

## Outbox Pattern Implementation

The outbox ensures exactly-once delivery by storing outgoing messages in the same SurrealDB transaction as your domain data:

```csharp
public async Task Handle(SubmitOrder command, IMessageBus bus, AeroDbSession session)
{
    var order = new Order(command.OrderId, command.Amount);

    // Domain write in AeroDB
    await session.Create("order", order);

    // Outbox message — delivered only if the order write succeeds
    await bus.PublishAsync(new OrderPlaced(order.Id));
}
```

Wolverine flushes the outbox in a background coordinator, guaranteeing delivery even if the process crashes after the domain write but before the transport send.

## Saga Persistence

Wolverine sagas store their state in SurrealDB:

```csharp
public class OrderSaga : Saga
{
    public long Id { get; set; }
    public decimal Total { get; set; }
    public bool Paid { get; set; }

    public void Handle(OrderSubmitted submitted, AeroDbSession session)
    {
        Total = submitted.Amount;
    }
}
```

Saga state uses the same durable store; no extra configuration required beyond `PersistMessagesWithAeroDb()`.

## Scheduled and Delayed Messages

Scheduled messages are stored in SurrealDB and executed when their execution time arrives:

```csharp
// Schedule a message 30 minutes in the future
await bus.ScheduleAsync(
    new PaymentReminder(order.Id),
    TimeSpan.FromMinutes(30)
);

// Schedule at a specific UTC time
await bus.ScheduleAsync(
    new SubscriptionExpiry(subscription.Id),
    subscription.ExpiresAt
);
```

The Wolverine scheduled message coordinator queries SurrealDB for due messages on a configurable polling interval.

## Dead Letter Queues

Failed messages are automatically moved to a dead letter queue (DLQ) stored in SurrealDB:

```csharp
opts.Policies.OnException<TimeoutException>()
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
    .Then.MoveToErrorQueue();

// DLQ records can be inspected and replayed:
opts.Policies.DeadLetterQueue.EnableForAllEndpoints();
```

DLQ records include the original message, exception details, and failure timestamps for operational debugging.

## Wolverine Handler Configuration

```csharp
// Handler with mixed AeroDB and Wolverine dependencies
public class CreateUserHandler
{
    public async Task Handle(CreateUser command, AeroDbSession session, IMessageBus bus)
    {
        var user = new User { Email = command.Email };
        await session.Create("user", user);
        await bus.PublishAsync(new UserCreated(user.Id));
    }
}
```

Register Wolverine with AeroDB persistence:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithAeroDb("ws://localhost:8000", "aero", "wolverine");
    opts.PublishAllMessages().ToAeroDbQueue("default");
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
});
```

## See Also

- [ASP.NET Identity](/docs/integrations/aspnet-identity)
- [Health Checks](/docs/integrations/health-checks)
