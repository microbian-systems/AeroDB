---
title: Time Series
description: Time-series queries and analysis in AeroDB
---

AeroDB supports time-series data through SurrealDB's temporal functions and LINQ aggregations. Time bucketing, windowing, downsampling, and range queries are expressed fluently through `session.Query<T>()`.

## Setting Up Time-Series Tables

Configure indexes on timestamp and sensor fields for query performance:

```csharp
options.Schema.For<SensorReading>()
    .Identity(s => s.Id)
    .Index(s => s.SensorId)
    .Index(s => s.Timestamp)
    .Index(s => new { s.SensorId, s.Timestamp });  // Composite for range scans
```

Define your time-series entity:

```csharp
public class SensorReading : EntitySnowflake
{
    public string SensorId { get; set; }
    public DateTime Timestamp { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public double Pressure { get; set; }
    public double? WindSpeed { get; set; }
}
```

### Multi-Tenant Time Series

```csharp
options.Schema.For<SensorReading>()
    .MultiTenanted()
    .Index(s => new { s.TenantId, s.SensorId, s.Timestamp });
```

## Time Range Queries

### Between (Date Range)

Query sensor readings within a specific time window:

```csharp
var start = DateTime.UtcNow.AddHours(-24);
var end = DateTime.UtcNow;

var readings = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start && s.Timestamp <= end)
    .Where(s => s.SensorId == "sensor-01")
    .OrderBy(s => s.Timestamp)
    .ToListAsync();
```

Partial date boundaries:

```csharp
var today = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= DateTime.Today
             && s.Timestamp < DateTime.Today.AddDays(1))
    .ToListAsync();
```

## Bucketing by Time Intervals

Group readings into time buckets for periodic aggregation:

```csharp
var hourlyAverages = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start && s.Timestamp <= end)
    .GroupBy(s => new
    {
        Bucket = s.Timestamp.Hour,
        Sensor = s.SensorId
    })
    .Select(g => new
    {
        Hour = g.Key.Bucket,
        Sensor = g.Key.Sensor,
        AvgTemp = g.Average(s => s.Temperature),
        MinTemp = g.Min(s => s.Temperature),
        MaxTemp = g.Max(s => s.Temperature),
        AvgHumidity = g.Average(s => s.Humidity),
        ReadingCount = g.Count()
    })
    .OrderBy(r => r.Hour)
    .ToListAsync();
```

### Daily Bucketing

```csharp
var dailyMetrics = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= lastMonth)
    .GroupBy(s => new
    {
        Day = s.Timestamp.Date,
        Sensor = s.SensorId
    })
    .Select(g => new
    {
        g.Key.Day,
        g.Key.Sensor,
        AvgTemp = g.Average(s => s.Temperature),
        MinTemp = g.Min(s => s.Temperature),
        MaxTemp = g.Max(s => s.Temperature),
        Readings = g.Count()
    })
    .OrderBy(r => r.Day)
    .ToListAsync();
```

### Monthly / Custom Bucketing

```csharp
var monthlyTrends = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= yearAgo)
    .GroupBy(s => new
    {
        Year = s.Timestamp.Year,
        Month = s.Timestamp.Month
    })
    .Select(g => new
    {
        Year = g.Key.Year,
        Month = g.Key.Month,
        AvgPressure = g.Average(s => s.Pressure),
        MaxWind = g.Max(s => s.WindSpeed ?? 0)
    })
    .OrderBy(r => r.Year)
    .ThenBy(r => r.Month)
    .ToListAsync();
```

## Aggregations

### Average, Sum, Min, Max

Window-specific aggregates:

```csharp
var stats = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01"
             && s.Timestamp >= lastHour)
    .Select(s => new
    {
        AvgTemp = s.Temperature.Average(),
        MaxTemp = s.Temperature.Max(),
        MinTemp = s.Temperature.Min(),
        TotalReadings = s.Count()
    })
    .FirstOrDefaultAsync();
```

### Aggregations Over Buckets

```csharp
var bucketStats = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start)
    .GroupBy(s => s.SensorId)
    .Select(g => new
    {
        Sensor = g.Key,
        AvgTemp = g.Average(s => s.Temperature),
        SumPressure = g.Sum(s => s.Pressure),
        MinHumidity = g.Min(s => s.Humidity),
        MaxHumidity = g.Max(s => s.Humidity),
        ReadingCount = g.Count()
    })
    .ToListAsync();
```

## Windowing Functions

Calculate moving averages and sliding-window aggregations:

```csharp
var smoothed = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01")
    .OrderBy(s => s.Timestamp)
    .Window(w => w
        .PartitionBy(s => s.SensorId)
        .OrderBy(s => s.Timestamp)
        .RowsBetween(-5, 0))  // Current row + 5 preceding
    .Select(s => new
    {
        s.Timestamp,
        Temperature = s.Temperature,
        MovingAvg5 = s.Temperature.Average(),
        MovingMin5 = s.Temperature.Min(),
        MovingMax5 = s.Temperature.Max()
    })
    .ToListAsync();
```

### Larger Window Sizes

```csharp
var trend10 = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01" && s.Timestamp >= lastWeek)
    .OrderBy(s => s.Timestamp)
    .Window(w => w
        .PartitionBy(s => s.SensorId)
        .OrderBy(s => s.Timestamp)
        .RowsBetween(-10, 0))
    .Select(s => new
    {
        s.Timestamp,
        SmoothedTemp = s.Temperature.Average(),
        Volatility = s.Temperature.StandardDeviation()
    })
    .ToListAsync();
```

### Range-Based Windows

```csharp
var timeWindowed = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01")
    .OrderBy(s => s.Timestamp)
    .Window(w => w
        .PartitionBy(s => s.SensorId)
        .OrderBy(s => s.Timestamp)
        .RangeBetween(-TimeSpan.FromMinutes(30), TimeSpan.Zero))
    .Select(s => new
    {
        s.Timestamp,
        HalfHourAvg = s.Temperature.Average()
    })
    .ToListAsync();
```

## Downsampling

Reduce high-frequency data to lower-resolution summaries:

```csharp
var downsampled = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start && s.Timestamp <= end)
    .GroupBy(s => new
    {
        Day = s.Timestamp.Date,
        Sensor = s.SensorId
    })
    .Select(g => new
    {
        g.Key.Day,
        g.Key.Sensor,
        AvgTemp = g.Average(s => s.Temperature),
        MinTemp = g.Min(s => s.Temperature),
        MaxTemp = g.Max(s => s.Temperature),
        Samples = g.Count()
    })
    .OrderBy(r => r.Day)
    .ThenBy(r => r.Sensor)
    .ToListAsync();
```

### 15-Minute Downsampling

```csharp
var fifteenMin = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01" && s.Timestamp >= lastDay)
    .GroupBy(s => new
    {
        QuarterHour = s.Timestamp.Date.AddMinutes(
            15 * (s.Timestamp.Hour * 4 + s.Timestamp.Minute / 15)),
        Sensor = s.SensorId
    })
    .Select(g => new
    {
        Bucket = g.Key.QuarterHour,
        AvgTemp = g.Average(s => s.Temperature),
        MaxTemp = g.Max(s => s.Temperature)
    })
    .OrderBy(r => r.Bucket)
    .ToListAsync();
```

## CRUD on Time-Series Data

### Insert Time-Series Records

```csharp
var reading = new SensorReading
{
    Id = SnowflakeGenerator.NewId(),
    SensorId = "sensor-01",
    Timestamp = DateTime.UtcNow,
    Temperature = 22.5,
    Humidity = 45.0,
    Pressure = 1013.2
};
session.Store(reading);
await session.SaveChangesAsync();
```

### Batch Insert (Bulk Ingestion)

```csharp
var batch = Enumerable.Range(0, 1000).Select(i => new SensorReading
{
    Id = SnowflakeGenerator.NewId(),
    SensorId = "sensor-01",
    Timestamp = DateTime.UtcNow.AddSeconds(-i * 10),
    Temperature = 20.0 + Random.Shared.NextDouble() * 5,
    Humidity = 40.0 + Random.Shared.NextDouble() * 10
}).ToList();

session.Store(batch);
await session.SaveChangesAsync();
```

### Update a Time-Series Record

```csharp
var reading = await session.LoadAsync<SensorReading>("sensor_reading:abc123");
reading.Temperature = 23.1;
session.Store(reading);
await session.SaveChangesAsync();
```

### Delete Old Data

```csharp
// Purge records older than 90 days
await session.DeleteWhereAsync<SensorReading>(
    s => s.Timestamp < DateTime.UtcNow.AddDays(-90));
```

## Query Patterns

### Latest Reading Per Sensor

```csharp
var latestPerSensor = await session.Query<SensorReading>()
    .GroupBy(s => s.SensorId)
    .Select(g => new
    {
        Sensor = g.Key,
        LatestTemp = g.Max(s => s.Timestamp),
        Value = g.OrderByDescending(s => s.Timestamp)
                 .Select(s => s.Temperature)
                 .FirstOrDefault()
    })
    .ToListAsync();
```

### Anomaly Detection (Threshold Queries)

```csharp
var anomalies = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01"
             && s.Timestamp >= last24Hours
             && (s.Temperature > 35.0 || s.Temperature < -10.0
              || s.Humidity > 95.0))
    .OrderByDescending(s => s.Timestamp)
    .ToListAsync();
```

### Rate of Change

```csharp
var rateOfChange = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01")
    .OrderBy(s => s.Timestamp)
    .Window(w => w
        .PartitionBy(s => s.SensorId)
        .OrderBy(s => s.Timestamp)
        .RowsBetween(-1, 0))
    .Select(s => new
    {
        s.Timestamp,
        Temperature = s.Temperature,
        Delta = s.Temperature - s.Temperature.Lag()
    })
    .ToListAsync();
```
