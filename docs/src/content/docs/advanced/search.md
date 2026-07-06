---
title: Search
description: Full-text and vector search in AeroDB
---

## Full-Text Search Configuration

Define FTS indexes during store configuration:

```csharp
opts.Schema.For<Article>()
    .FullTextIndex(a => a.Title)
    .FullTextIndex(a => a.Body, "english");
```

Query with LINQ:

```csharp
var results = await session.Query<Article>()
    .Where(a => a.Body.Search("surreal database"))
    .ToListAsync();
```

Analyzers: `"english"` (stemming), `"french"` (accents), `"simple"` (lowercasing), `"keyword"` (exact match). Custom analyzers are defined via SurrealQL and referenced by name.

## Vector Indexes: HNSW vs DiskANN

| Algorithm | Profile | Best For |
|-----------|---------|----------|
| **HNSW** | In-memory graph | Fast lookups, <1M vectors |
| **DiskANN** | SSD-backed | Billion-scale, memory-constrained |

```csharp
opts.Schema.For<Product>()
    .VectorIndex(p => p.Embedding, cfg => cfg
        .WithAlgorithm(VectorAlgorithm.HNSW)
        .WithDimensions(384)
        .WithDistance(DistanceMetric.Cosine));
```

## Type, Hybrid Search, and AI-Powered Search

Vector types: `F32` (4B/dim), `F16` (2B/dim), `I8` (1B/dim). Hybrid:

```csharp
var results = await session.Query<Document>()
    .Where(d => d.Body.Search("annual report") &&
                d.Embedding.Nearest(searchVector, 0.75f))
    .OrderByScore()
    .ToListAsync();
```

SurrealML inference for server-side embeddings:

```csharp
opts.Schema.For<Article>()
    .VectorIndex(a => a.Embedding, cfg => cfg.WithInference("my-service"));

var results = await session.Query<Article>()
    .Where(a => a.Content.Nearest("What is SurrealDB?", 0.8f))
    .ToListAsync();
```

## RAG Pipeline and Scoring

```csharp
session.Store(new Document { Content = "...", Embedding = embedding });
await session.SaveChangesAsync();

var chunks = await session.Query<Document>()
    .Where(d => d.Content.Search(query) && d.Embedding.Nearest(vec, 0.7f))
    .Take(5).ToListAsync();

var ctx = string.Join("\n\n", chunks.Select(c => c.Content));
var answer = await llm.CompleteAsync($"Context: {ctx}\n\nQ: {query}");

// Weight FTS vs vector
var scored = await session.Query<Doc>()
    .Where(d => d.Body.Search("query") && d.Vec.Nearest(vec, 0.5f))
    .OrderByScore(weights: new ScoreWeights { Fts = 0.3, Vector = 0.7 })
    .ToListAsync();
```

Store embeddings as `float[]` or `ReadOnlyMemory<float>` — both serialize to `array<float>`.
