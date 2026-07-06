---
title: Time Series
description: Time-bucketed queries for temporal data
---

AeroDB exposes SurrealDB's time-series capabilities through a fluent builder API. Group data into buckets (hours, days, months) and run aggregations.

```csharp
// Query time-bucketed trade data
var dailyVolume = await session.Query<Trade>()
    .TimeSeries(t => t.Timestamp)
    .Bucket(TimeUnit.Day)
    .Aggregate(g => new
    {
        Date = g.Key,
        Volume = g.Sum(t => t.Quantity),
        High = g.Max(t => t.Price),
        Low = g.Min(t => t.Price),
        Open = g.First().Price,
        Close = g.Last().Price
    })
    .Where(g => g.Date > DateTimeOffset.UtcNow.AddDays(-30))
    .ToListAsync();

// Rolling window aggregations
var movingAverage = await session.Query<Metric>()
    .TimeSeries(m => m.Timestamp)
    .Bucket(TimeUnit.Hour)
    .Window(24) // 24-hour rolling window
    .Aggregate(g => new
    {
        Window = g.Key,
        Avg = g.Average(m => m.Value)
    })
    .ToListAsync();
```

**Full sample:** [`samples/CryptoTrader/`](https://github.com/microbian-systems/AeroDB/tree/main/samples/CryptoTrader)
