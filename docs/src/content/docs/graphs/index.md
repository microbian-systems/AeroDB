---
title: Graphs
description: Graph traversal and relationship queries in AeroDB
---

AeroDB provides first-class graph traversal through `session.Graph<T>()`, enabling edge navigation, path finding, and relationship queries backed by SurrealDB's native graph engine.

## Configuring Graph Relationships

Graph edges in SurrealDB are separate records with `IN` (source) and `OUT` (target) fields. Configure edge tables via `StoreOptions.Schema.Edge()`:

```csharp
options.Schema.Edge<Friendship, User, User>(edge =>
{
    edge.SetSchemaMode(SchemaMode.Flexible);
    edge.Index(e => e.Since);  // Index edge properties for filtering
});

options.Schema.Edge<Employment, User, Company>(edge =>
{
    edge.Index(e => e.Role);
    edge.Index(e => e.StartDate);
});
```

### Edge Record Types

Define edge types by extending `EdgeRecord`:

```csharp
public class Friendship : EdgeRecord
{
    public DateTime Since { get; set; }
    public string Type { get; set; } // "friend", "family", "colleague"
    public int Strength { get; set; } // 1-5
}

public class Employment : EdgeRecord
{
    public string Role { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}
```

Navigation properties on entities reference the edge type:

```csharp
public class User : EntitySnowflake
{
    public string Name { get; set; }
    public List<Friendship> FriendsWith { get; set; }
}
```

## Creating Graph Edges

Create relationships with `session.RelateAsync()`:

```csharp
var alice = await session.LoadAsync<User>("user:alice");
var bob = await session.LoadAsync<User>("user:bob");

var friendship = new Friendship
{
    Since = DateTime.UtcNow,
    Type = "friend",
    Strength = 4
};

await session.RelateAsync(alice, friendship, bob);
await session.SaveChangesAsync();
```

Batch create edges:

```csharp
foreach (var friend in newFriends)
{
    var edge = new Friendship { Since = DateTime.UtcNow, Type = "friend" };
    await session.RelateAsync(currentUser, edge, friend);
}
await session.SaveChangesAsync();
```

## Querying Graphs

### Out / In Traversal

Traverse outgoing edges from a starting node:

```csharp
var friends = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .Where(f => f.IsActive)
    .OrderBy(f => f.Name)
    .ToListAsync();
```

Traverse incoming edges:

```csharp
var followers = await session.Graph<User>()
    .In<User>(u => u.Followers)
    .ToListAsync();
```

### Multi-Hop Traversal

Chain `.Out()` / `.In()` for deeper traversal:

```csharp
var colleaguesOfFriends = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .Out<User>(u => u.WorksWith)
    .Distinct()
    .ToListAsync();
```

### Friend-of-Friends Example

```csharp
var currentUserId = "user:alice";
var friendsOfFriends = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)   // friends
    .Out<User>(u => u.FriendsWith)   // friends of friends
    .Where(f => f.Id != currentUserId)
    .Distinct()
    .ToListAsync();
```

With edge filtering at each hop:

```csharp
var closeFriendsOfFriends = await session.Graph<User>()
    .Out<User, Friendship>(u => u.FriendsWith, e => e.Strength >= 3)
    .Out<User, Friendship>(u => u.FriendsWith, e => e.Strength >= 3)
    .Where(f => f.Id != currentUserId)
    .Distinct()
    .ToListAsync();
```

### Edge Filtering

Filter both vertices and edges in the same traversal:

```csharp
var recentConnections = await session.Graph<User>()
    .Out<User, Friendship>(u => u.FriendsWith,
        e => e.Since >= DateTime.UtcNow.AddMonths(-6)
          && e.Type == "friend"
          && e.Strength > 2)
    .Where(f => f.IsActive)
    .ToListAsync();
```

## Path Finding

### Shortest Path

Find the shortest path between two nodes with optional depth limit:

```csharp
var path = await session.Graph<User>()
    .ShortestPath()
    .From("user:alice")
    .To("user:bob")
    .MaxDepth(6)
    .FirstOrDefaultAsync();
// Returns the shortest chain of user nodes
```

### All Paths

Find all paths with constraints:

```csharp
var paths = await session.Graph<User>()
    .AllPaths()
    .From("user:alice")
    .To("user:bob")
    .MaxDepth(4)
    .ToListAsync();
// Returns all possible paths up to 4 hops deep
```

### Path with Edge Constraints

```csharp
var paths = await session.Graph<User>()
    .AllPaths()
    .From("user:alice")
    .To("user:bob")
    .WhereEdge<Friendship>(e => e.Strength >= 2)
    .MaxDepth(6)
    .ToListAsync();
```

## IncludeEdges for Metadata

Return graph results with edge metadata attached to each vertex:

```csharp
var graphResult = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .IncludeEdges()
    .ToListAsync();
// Each result contains both the vertex (User) and the edge properties (Friendship)

// Access edge data via the result wrapper
foreach (var result in graphResult)
{
    Console.WriteLine($"Friend: {result.Vertex.Name}");
    Console.WriteLine($"Since: {result.Edge.Since}");
    Console.WriteLine($"Strength: {result.Edge.Strength}");
}
```

## CRUD on Graph Edges

### Update Edge Properties

```csharp
// Load the edge by its ID
var edge = await session.LoadEdgeAsync<Friendship>("friendship:abc123");
edge.Strength = 5;
edge.Type = "close_friend";
session.Store(edge);
await session.SaveChangesAsync();
```

### Delete an Edge

```csharp
// Disconnect two nodes by removing the edge
await session.DisconnectAsync<User>("user:alice", "user:bob", u => u.FriendsWith);
// Or delete the edge record directly
await session.DeleteEdgeAsync<Friendship>("friendship:abc123");
```

### Query Edges Directly

```csharp
// Query edges as regular documents
var recentFriendships = await session.Query<Friendship>()
    .Where(e => e.Since >= DateTime.UtcNow.AddDays(-30))
    .OrderByDescending(e => e.Since)
    .ToListAsync();
```

## Advanced Graph Patterns

### Multi-Type Traversal

Traverse through different edge types to reach different node types:

```csharp
var results = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .Out<Company>(u => u.WorksAt)
    .Where(c => c.Industry == "Technology")
    .ToListAsync();
```

### Cyclic Graph Detection

Detect cycles by tracking visited nodes:

```csharp
var cyclePath = await session.Graph<User>()
    .AllPaths()
    .From("user:alice")
    .To("user:alice")     // Same node = cycle detection
    .MaxDepth(10)
    .FirstOrDefaultAsync();
```

### Graph Statistics

```csharp
var degree = await session.Graph<User>()
    .Out<User>(u => u.FriendsWith)
    .CountAsync();

var mutualConnections = await session.Query<Friendship>()
    .CountAsync(e => e.Strength >= 4);
```
