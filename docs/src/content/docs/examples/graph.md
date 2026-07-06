---
title: Graph
description: Traverse relationships with graph edges and paths
---

SurrealDB's graph engine lets you model and traverse relationships directly. AeroDB exposes this through a fluent API with `Edge<,>` types and graph query extensions.

## Defining Edges

```csharp
// Define an edge between Person and Article
public sealed record Authored : Graph.Edge<Person, Article>;

// Store a relationship
var person = await session.LoadAsync<Person>("person:alice");
var article = new Article { Title = "Graph Queries in SurrealDB" };
session.Store(article);

// Relate them
session.StoreEdge<Authored>(person, article);
await session.SaveChangesAsync();
```

## Graph Queries

```csharp
// Find all articles authored by a person
var articles = await session.Query<Authored>()
    .Where(e => e.In == "person:alice")
    .Select(e => e.Out)
    .OfType<Article>()
    .ToListAsync();

// Path traversal: find friends-of-friends
var connections = await session.Query<Person>()
    .Where(p => p.Name == "Alice")
    .GraphPath(p => p.Friends, depth: 2)
    .ToListAsync();
```

**Full sample:** [`samples/WikipediaSearch/`](https://github.com/microbian-systems/AeroDB/tree/main/samples/WikipediaSearch)
