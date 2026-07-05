---
title: Create an Index
description: Define schema indexes for performance and search
---

## Goal

Add a full-text search index on article content and a unique index on user email.

## Code

```csharp
using AeroDB;

var store = DocumentStore.For(cfg =>
{
    cfg.Connection("ws://localhost:8000/rpc");
    cfg.Database("myapp");
    cfg.Namespace("production");

    // Full-text search index on Article
    cfg.Schema.For<Article>()
        .FullTextIndex(idx => idx
            .On(a => a.Title, a => a.Body)
            .WithAnalyzer("english")
        );

    // B-tree unique index on email
    cfg.Schema.For<User>()
        .UniqueIndex(UniqueIndexType.Computed, nameof(User.Email))
        .IndexOn(u => u.Email);

    // Vector index for semantic search
    cfg.Schema.For<WikipediaArticle>()
        .VectorIndex("embedding", 768);

    // Compound index for common queries
    cfg.Schema.For<Order>()
        .Index(x => x.CustomerId)
        .Include(x => x.Status);
});
```

## Explanation

- `FullTextIndex()` creates an analyzer-backed search index that supports ranking and relevance scoring.
- `UniqueIndex()` enforces uniqueness at the database level.
- `VectorIndex()` stores embedding vectors for cosine-similarity search.
- `Index()` with `.Include()` creates a compound covering index for common query patterns.
- Indexes are applied on first usage when `AutoCreate.All` is set, or via `await store.Storage.ApplyAllConfiguredIndexesToDatabaseAsync()`.

## See Also

- [Full-Text Search](/examples/fulltext-search/)
- [API: SchemaOptions](/api/AeroDB.SchemaOptions)
