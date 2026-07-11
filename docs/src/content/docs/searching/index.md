---
title: Searching
description: Full-text, vector, and hybrid search in AeroDB
---

AeroDB integrates SurrealDB's full-text, vector, and hybrid search capabilities through a unified LINQ API — backed by configurable analyzers, BM25 scoring, HNSW/DiskANN vector indexes, and SurrealML AI integration.

## Configuring Search Indexes

All search indexes are configured during store initialization via `StoreOptions.Schema.For<T>()`.

### Full-Text Search Index

```csharp
options.Schema.For<Article>()
    .FullTextIndex(a => a.Title, analyzer: "english")
    .FullTextIndex(a => a.Body, analyzer: "english",
        bm25: (k1: 1.2, b: 0.75));
```

Multi-field FTS index (tokenized together):

```csharp
options.Schema.For<Article>()
    .FullTextIndex("english", a => a.Title, a => a.Body, a => a.Tags);
```

### Custom Analyzers

Define custom analyzers with tokenizers and filters before referencing them in indexes:

```csharp
options.Schema.Analyzers.DefineAnalyzer(
    name: "custom_english",
    tokenizers: [
        Search.Tokenizer.Blank,
        Search.Tokenizer.Class,
        Search.Tokenizer.Punct
    ],
    filters: [
        Search.Filter.Lowercase,
        Search.Filter.SnowballEnglish,
        Search.Filter.Ascii
    ]);

options.Schema.For<Article>()
    .FullTextIndex(a => a.Body, analyzer: "custom_english");
```

Available tokenizers: `blank`, `class`, `camel`, `punct`, `whitespace`, `edgengram-2`, `edgengram-3`.

Available filters: `SNOWBALL(en|fr|de|es)`, `LOWERCASE`, `UPPERCASE`, `ASCII`, `NGRAMS(2)`, `NGRAMS(3)`.

### HNSW Vector Index

Hierarchical navigable small world graphs for in-memory approximate nearest-neighbour search:

```csharp
options.Schema.For<Document>()
    .HnswIndex(d => d.Embedding,
        dimension: 1536,                 // OpenAI ada-002 dimensionality
        distance: Search.Distance.Cosine);
```

Available distance metrics:

| Constant | Description |
|----------|-------------|
| `Search.Distance.Cosine` | Cosine distance (default) |
| `Search.Distance.Euclidean` | Euclidean (L2) distance |
| `Search.Distance.Manhattan` | Manhattan (L1) distance |
| `Search.Distance.Minkowski` | Minkowski distance |
| `Search.Distance.Hamming` | Hamming distance |
| `Search.Distance.Jaccard` | Jaccard distance |
| `Search.Distance.InnerProduct` | Inner product distance |
| `Search.Distance.CosineNormalized` | Cosine on L2-normalized vectors |

### DiskANN Vector Index (SurrealDB 3.1+)

Disk-based approximate nearest-neighbour index for very large embedding sets that exceed available RAM:

```csharp
options.Schema.For<Document>()
    .DiskannIndex(
        d => d.Embedding,
        dimension: 1536,
        vectorType: "F32",              // F32 (default), F16, I8, U8
        distance: Search.Distance.Cosine,
        degree: 64,                     // Graph degree (default 64)
        lBuild: 100,                    // Construction search list (default 100)
        alpha: 1.2,                     // Pruning parameter (default 1.2)
        hashedVector: false);           // Hash-stabilized keys
```

See the [DiskANN SurrealDB documentation](https://surrealdb.com/docs/surrealql/statements/define/indexes#diskann-index) for detailed parameter tuning guidance.

### Hybrid Search Setup

Convenience helper that configures both FTS and HNSW indexes:

```csharp
options.Schema.For<Document>()
    .HybridSearch(
        textFields: [("Title", 0.3), ("Body", 0.7)],
        vectorField: d => d.Embedding,
        dimension: 1536,
        analyzer: "english",
        distance: Search.Distance.Cosine);
```

## Full-Text Search (FTS)

### Basic FTS Query

```csharp
var results = await session.Query<Article>()
    .Where(a => a.Body.Search("machine learning"))
    .OrderByDescending(a => a.Body.Relevance())
    .ToListAsync();
```

### FTS with Analyzer and Highlighting

```csharp
var highlighted = await session.Query<Article>()
    .Where(a => a.Body.Search("quantum computing", cfg => cfg
        .WithAnalyzer("english")
        .WithHighlighting()))
    .OrderByDescending(a => a.Body.Relevance())
    .Select(a => new
    {
        a.Id,
        a.Title,
        Snippet = a.Body.Highlight()
    })
    .ToListAsync();
```

### BM25 Scoring Customization

BM25 parameters (k1, b) configured at index creation time control term-frequency saturation and document-length normalization:

```csharp
options.Schema.For<Article>()
    .FullTextIndex(a => a.Body, analyzer: "english",
        bm25: (k1: 1.5, b: 0.5));  // Higher k1 = slower saturation
```

## HNSW Vector Search

### Nearest Neighbors

```csharp
var embedding = await GenerateEmbeddingAsync("quantum computing");
var results = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10, cfg => cfg
        .WithIndexType(IndexType.Hnsw)
        .WithDistanceMetric(DistanceMetric.Cosine)))
    .Select(d => new { d.Id, d.Title, Score = d.Embedding.SimilarityTo(embedding) })
    .ToListAsync();
```

### Vector Similarity Scoring

Return similarity scores alongside results:

```csharp
var scored = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(queryEmbedding, k: 20))
    .Select(d => new
    {
        d.Id,
        d.Title,
        Score = d.Embedding.SimilarityTo(queryEmbedding)
    })
    .OrderByDescending(d => d.Embedding.SimilarityTo(queryEmbedding))
    .ToListAsync();
```

## DiskANN Vector Search (SurrealDB 3.1+)

```csharp
var results = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10, cfg => cfg
        .WithIndexType(IndexType.DiskAnn)
        .WithRecallTarget(0.99)       // Higher = better accuracy, slower query
        .WithDistanceMetric(DistanceMetric.Cosine)))
    .ToListAsync();
```

DiskANN is ideal when your embedding set exceeds available memory. Tune `recallTarget` between 0.90–0.99 based on latency/accuracy requirements.

## Hybrid Search (FTS + Vector)

### Combined Scoring with Weights

Combine full-text and vector search with weighted scoring for balanced relevance:

```csharp
var hybrid = await session.Query<Document>()
    .Where(d => d.Body.Search("deep learning")
             || d.Embedding.NearestNeighbors(embedding, k: 50))
    .OrderByDescending(d =>
        d.Body.Relevance().Weighted(0.3) +
        d.Embedding.SimilarityTo(embedding).Weighted(0.7))
    .Take(20)
    .ToListAsync();
```

### AND Hybrid (Strict Matching)

Require both text relevance and vector similarity:

```csharp
var andHybrid = await session.Query<Document>()
    .Where(d => d.Body.Search("deep learning")
             && d.Embedding.NearestNeighbors(embedding, k: 10))
    .OrderByDescending(d =>
        d.Body.Relevance().Weighted(0.4) +
        d.Embedding.SimilarityTo(embedding).Weighted(0.6))
    .ToListAsync();
```

### Weight Tuning

Adjust weights per use case:

- **Semantic search**: `Weighted(0.1)` text + `Weighted(0.9)` vector
- **Keyword-heavy**: `Weighted(0.7)` text + `Weighted(0.3)` vector
- **Balanced**: `Weighted(0.4)` text + `Weighted(0.6)` vector

```csharp
var semantic = await session.Query<Document>()
    .Where(d => d.Body.Search(userQuery)
             || d.Embedding.NearestNeighbors(queryEmbedding, k: 20))
    .OrderByDescending(d =>
        d.Body.Relevance().Weighted(0.15) +
        d.Embedding.SimilarityTo(queryEmbedding).Weighted(0.85))
    .Take(10)
    .ToListAsync();
```

## AI Integration: SurrealML

### Embedding Generation via ml:: Inference

Generate embeddings inline using SurrealML inference pipelines:

```csharp
var pipeline = session.Ml().Pipeline("text-embedding-ada");
var embedding = await pipeline.InferAsync(new
{
    input = "What is quantum entanglement?"
});

// Use the embedding immediately in vector search
var similar = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(embedding, k: 10))
    .Select(d => new { d.Id, d.Title, d.Content })
    .ToListAsync();
```

### RAG Pipeline

Retrieve context documents for a retrieval-augmented generation pipeline:

```csharp
// 1. Generate query embedding
var queryEmbedding = await session.Ml()
    .Pipeline("text-embedding-ada")
    .InferAsync(new { input = userQuestion });

// 2. Retrieve relevant context
var contextDocs = await session.Query<Document>()
    .Where(d => d.Embedding.NearestNeighbors(queryEmbedding, k: 5))
    .Select(d => d.Content)
    .ToListAsync();

// 3. Rerank with SurrealML
var reranked = await session.Ml()
    .Infer("rerank", new
    {
        query = userQuestion,
        documents = contextDocs
    })
    .ToListAsync();

// 4. Context is now ready for LLM prompt construction
var context = string.Join("\n---\n", contextDocs);
```

### Classification and NLP Pipelines

```csharp
// Sentiment analysis via SurrealML
var sentiment = await session.Ml()
    .Infer("sentiment", new { text = "This product is amazing!" })
    .FirstOrDefaultAsync();

// Text classification
var category = await session.Ml()
    .Infer("classify", new
    {
        text = article.Body,
        labels = new[] { "tech", "science", "politics", "sports" }
    })
    .FirstOrDefaultAsync();
```

## Relevance Ranking and Scoring

### Scoring Functions

```csharp
// BM25 relevance score
var ftsScore = d.Body.Relevance();

// Vector similarity (0–1 range)
var vecScore = d.Embedding.SimilarityTo(queryEmbedding);

// Normalized distance
var normalizedDist = d.Embedding.DistanceTo(queryEmbedding);
```

### Weighted Combined Score

```csharp
.OrderByDescending(d =>
    d.Body.Relevance().Weighted(textWeight) +
    d.Embedding.SimilarityTo(embedding).Weighted(vectorWeight))
```

### Debug Query Scores

```csharp
var debugResults = await session.Query<Document>()
    .Where(d => d.Body.Search("machine learning")
             || d.Embedding.NearestNeighbors(queryEmbedding, k: 10))
    .Select(d => new
    {
        d.Id,
        d.Title,
        FtsScore = d.Body.Relevance(),
        VecScore = d.Embedding.SimilarityTo(queryEmbedding),
        CombinedScore =
            d.Body.Relevance().Weighted(0.3) +
            d.Embedding.SimilarityTo(queryEmbedding).Weighted(0.7)
    })
    .OrderByDescending(d => d.CombinedScore)
    .ToListAsync();
```

## Query Diagnostics

Inspect generated SurrealQL for any search query:

```csharp
var query = session.Query<Document>()
    .Where(d => d.Body.Search("machine learning")
             || d.Embedding.NearestNeighbors(embedding, k: 10));

var (surrealql, parameters) = query.ToDebugString();
// Output includes full-text SEARCH clause and vector <|K, EF|> operator
```
