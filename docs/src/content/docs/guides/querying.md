---
title: Querying
description: Query documents in AeroDB
---

AeroDB's querying layer spans regular LINQ queries, graph traversals, time-series analysis, and full-text/vector search — all translated to SurrealQL.

## Regular Queries

Basic LINQ operations on any entity:

```csharp
var query = session.Query<User>()
    .Where(u => u.Age >= 21 && u.Status == "active")
    .OrderBy(u => u.Name)
    .Skip(10).Take(20);

var results = await query.ToListAsync();
```

Projections to DTOs:

```csharp
var summaries = await session.Query<User>()
    .Where(u => u.Department == "Engineering")
    .Select(u => new UserSummary { Id = u.Id, Name = u.Name })
    .ToListAsync();
```

Singles and aggregates:

```csharp
var user = await session.Query<User>()
    .FirstOrDefaultAsync(u => u.Email == "alice@example.com");

var count = await session.Query<User>().CountAsync(u => u.IsActive);
var anyPremium = await session.Query<User>().AnyAsync(u => u.Tier == "premium");
```

Include related entities:

```csharp
var orders = await session.Query<Order>()
    .Include(o => o.Customer)
    .Include(o => o.LineItems)
    .ThenInclude(li => li.Product)
    .ToListAsync();
```

## Graph Queries

Graph traversals use `Graph<T>()` for recursive edge navigation:

```csharp
// Friend-of-friends
var friendsOfFriends = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .Out<User>(u => u.FriendsWith)
    .Where(f => f.Id != userId)
    .ToListAsync();
```

Edge filtering by properties:

```csharp
var recentConnections = await session.Graph<User>()
    .Out<User, Friendship>(u => u.FriendsWith, e => e.Since >= DateTime.UtcNow.AddMonths(-6))
    .ToListAsync();
```

Shortest path between two nodes:

```csharp
var path = await session.Graph<User>()
    .ShortestPath()
    .From("user:alice")
    .To("user:bob")
    .MaxDepth(6)
    .FirstOrDefaultAsync();
```

All paths with constraints:

```csharp
var paths = await session.Graph<User>()
    .AllPaths()
    .From("user:alice")
    .To("user:bob")
    .MaxDepth(4)
    .ToListAsync();
```

Returning graph results with edge metadata:

```csharp
var graphResult = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .IncludeEdges()
    .ToListAsync();
// graphResult contains both vertices and edge properties
```

## Time-Series Queries

Bucket sensor readings by time intervals and aggregate:

```csharp
var hourlyAverages = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start && s.Timestamp <= end)
    .GroupBy(s => s.Timestamp.Hour)
    .Select(g => new
    {
        Hour = g.Key,
        AvgTemp = g.Average(s => s.Temperature),
        MaxHumidity = g.Max(s => s.Humidity),
        Count = g.Count()
    })
    .ToListAsync();
```

Windowing for moving averages:

```csharp
var smoothed = await session.Query<SensorReading>()
    .Where(s => s.SensorId == "sensor-01")
    .OrderBy(s => s.Timestamp)
    .Window(w => w
        .PartitionBy(s => s.SensorId)
        .OrderBy(s => s.Timestamp)
        .RowsBetween(-5, 0))
    .Select(s => new
    {
        s.Timestamp,
        MovingAvg = s.Temperature.Average()
    })
    .ToListAsync();
```

Downsampling with time bucketing:

```csharp
var downsampled = await session.Query<SensorReading>()
    .Where(s => s.Timestamp >= start)
    .GroupBy(s => new {
        Day = s.Timestamp.Date,
        Sensor = s.SensorId
    })
    .Select(g => new
    {
        g.Key.Day,
        g.Key.Sensor,
        AvgTemp = g.Average(s => s.Temperature),
        MinTemp = g.Min(s => s.Temperature),
        MaxTemp = g.Max(s => s.Temperature)
    })
    .OrderBy(r => r.Day)
    .ToListAsync();
```

## Search Queries

### Full-Text Search

Use `SearchAsync` with a configured FTS index:

```csharp
var results = await session.Query<Article>()
    .Where(a => a.SearchText.Search("machine learning", cfg => cfg
        .WithAnalyzer("english")
        .WithHighlighting()))
    .OrderByDescending(a => a.SearchText.Relevance())
    .ToListAsync();
```

### Vector Search (HNSW)

```csharp
var embedding = await GenerateEmbeddingAsync("quantum computing");
var results = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10, cfg => cfg
        .WithIndexType(IndexType.Hnsw)
        .WithDistanceMetric(DistanceMetric.Cosine)))
    .Select(d => new { d.Id, d.Title, Score = d.Embedding.SimilarityTo(embedding) })
    .ToListAsync();
```

### Vector Search (DiskANN) — SurrealDB 3.1+

```csharp
var results = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10, cfg => cfg
        .WithIndexType(IndexType.DiskAnn)
        .WithRecallTarget(0.99)))
    .ToListAsync();
```

### Hybrid Search (FTS + Vector)

Combine full-text and vector search with weighted scoring:

```csharp
var hybrid = await session.Query<Document>()
    .Where(d => d.SearchText.Search("deep learning") 
             || d.Embedding.NearestNeighbors(embedding, k: 50))
    .OrderByDescending(d => 
        d.SearchText.Relevance().Weighted(0.3) +
        d.Embedding.SimilarityTo(embedding).Weighted(0.7))
    .Take(20)
    .ToListAsync();
```

### RAG Pipelines and SurrealML

AeroDB integrates with SurrealML for AI-powered querying:

```csharp
// Retrieve context for RAG
var contextDocs = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(queryEmbedding, k: 5))
    .Select(d => d.Content)
    .ToListAsync();

// SurrealML inference for reranking
var reranked = await session.Ml()
    .Infer("rerank", new { query = "quantum computing", documents = contextDocs })
    .ToListAsync();
```

### Embedding Generation via ml:: Inference

Generate embeddings inline using ml:: inference pipelines:

```csharp
var pipeline = session.Ml().Pipeline("text-embedding-ada");
var embedding = await pipeline.InferAsync(new { input = "What is quantum entanglement?" });
// Use embedding immediately in vector search
var similar = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10))
    .ToListAsync();
```
