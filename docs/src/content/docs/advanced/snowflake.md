---
title: Snowflake IDs
description: Time-sortable unique identifiers
---

## What Are Snowflake IDs

64-bit, time-sortable, distributed unique identifiers:

```
 0  0000000000  00000  00000  000000000000
|1|-- 41 bits timestamp --|-5 wk-|-5 dc-|-- 12 bits sequence --|
```

- **41 bits**: Millisecond timestamp (custom epoch)
- **10 bits**: Worker (5) + Datacenter (5) = 1024 workers
- **12 bits**: Sequence (4096 IDs/ms/worker)

Generates ~4M IDs/sec with no coordination.

## Why Snowflake Over GUIDs

| Feature | Snowflake (long) | GUID/UUID |
|---------|-----------------|-----------|
| Sort order | Time-sortable | Random |
| Index perf | Clustered-friendly | Page splits |
| Storage | 8 bytes | 16 bytes |

Snowflake IDs are **monotonically increasing**, keeping B-tree indexes compact.

## How AeroDB Generates Them

```csharp
using Aero.Core;
long id = Snowflake.NewId();
```

Default epoch: **2024-01-01T00:00:00Z**.

## Configuration Options

```csharp
opts.Snowflake.Configure(g => g
    .WithWorkerId(1)         // 0-31
    .WithDatacenterId(0)     // 0-31
    .WithCustomEpoch(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
```

Set unique `WorkerId` per instance for multi-node safety.

## Using Snowflake.NewId() Explicitly

```csharp
var user = new User { Id = Snowflake.NewId(), Name = "Alice" };
session.Store(user);
await session.SaveChangesAsync();
```

## Entity Base Classes

```csharp
public abstract class Entity<TId> : IEntity<TId>
{
    public TId Id { get; set; }
}
public abstract class EntitySnowlake : Entity<long> { }
public abstract class EntityString  : Entity<string> { }
public abstract class EntityInt     : Entity<int> { }
public abstract class EntityGuid    : Entity<Guid> { }
```

Inherit for Snowflake-backed documents. When `Id == 0`, AeroDB auto-assigns a Snowflake ID before persisting.

```csharp
public class Order : EntitySnowlake
{
    public string CustomerName { get; set; }
    public decimal Total { get; set; }
}
```

## Record ID Format and Range Queries

SurrealDB record ID: `order:18446744073709551615`. Load by raw value:

```csharp
var order = await session.LoadAsync<Order>(18446744073709551615);
```

Snowflake IDs encode creation time for efficient scans without a `CreatedAt` field:

```csharp
var since = Snowflake.ForTimestamp(DateTime.UtcNow.AddHours(-1));
var recent = await session.Query<Order>()
    .Where(o => o.Id >= since)
    .OrderBy(o => o.Id)
    .ToListAsync();
```

`Snowflake.ForTimestamp()` generates lower-bound IDs — the PK index clusters by creation order naturally.
