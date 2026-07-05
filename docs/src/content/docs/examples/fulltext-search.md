---
title: Full-Text Search
description: Search indexes, analyzers, and vector search
---

AeroDB exposes SurrealDB's full-text search and vector search capabilities through a fluent builder API for index definitions and query methods.

## Defining Search Indexes

```csharp
storeOptions.Schema.For<Article>()
    .FullTextIndex(
        index => index
            .On(a => a.Title, a => a.Body)
            .WithAnalyzer("english")
            .SearchField(a => a.Title, a => a.Body)
    );
```

## Search Queries

```csharp
// Full-text search with ranking
var results = await session.Search<Article>()
    .Where(a => a.Matches("SurrealDB graph queries"))
    .OrderByScore()
    .Take(20)
    .ToListAsync();

// Hybrid search with filters
var results = await session.Search<Article>()
    .Where(a => a.Matches("document database"))
    .Filter(a => a.PublishedAt > DateTimeOffset.UtcNow.AddYears(-1))
    .ToListAsync();
```

## Vector Search

```csharp
// Define a vector index for semantic search
storeOptions.Schema.For<WikipediaArticle>()
    .VectorIndex("embedding", 768); // embedding dimension

// Semantic search using embeddings
float[] queryEmbedding = await embeddingService.EmbedAsync("graph databases");
var results = await session.Search<WikipediaArticle>()
    .Vector(queryEmbedding, field: "embedding")
    .Take(10)
    .ToListAsync();
```

**Full sample:** [`samples/WikipediaSearch/`](https://github.com/microbians/AeroDB/tree/main/samples/WikipediaSearch)
