---
title: Querying
description: Schema setup and fluent querying in AeroDB
---

AeroDB's querying layer builds on SurrealDB's native capabilities, exposing a fluent LINQ API for type-safe queries and a `Schema.For<T>()` system for index and schema configuration.

## Schema Configuration

Before querying, configure schema and indexes via `StoreOptions.Schema.For<T>()`. This defines how tables and indexes are created during store initialization.

### Identity Configuration

Designate the primary key property. For types that extend `Record`, the `Id` property is always the primary key; `Identity()` is present for explicit documentation and future extensibility:

```csharp
options.Schema.For<User>()
    .Identity(u => u.Id);
```

Supported identity types: `long`, `int`, `ulong`, `uint`, `string`, `Guid`, `byte`, `short`, `DateTime`.

### Schema Mode

Documents default to `SCHEMAFULL` (Strict). Use `SCHEMALESS` when you need flexible fields:

```csharp
options.Schema.For<User>()
    .SetSchemaMode(SchemaMode.Flexible);
```

### Standard Indexes

Simple btree index (SurrealDB default):

```csharp
options.Schema.For<User>()
    .Index(u => u.Email);
```

Unique index:

```csharp
options.Schema.For<User>()
    .UniqueIndex(u => u.Email);
```

Multi-column / composite index:

```csharp
options.Schema.For<User>()
    .Index(u => new { u.TenantId, u.Status }, cfg => cfg.IsUnique());
```

Custom index name:

```csharp
options.Schema.For<User>()
    .Index(u => u.Email, cfg => cfg.WithName("idx_user_email"));
```

### Full-Text Index

Requires an analyzer (use a built-in like `"english"` or define a custom one):

```csharp
options.Schema.For<Article>()
    .FullTextIndex(a => a.Body, analyzer: "english", bm25: (k1: 1.2, b: 0.75));
```

Multi-field FTS index:

```csharp
options.Schema.For<Article>()
    .FullTextIndex("english", a => a.Title, a => a.Body);
```

### HNSW Vector Index

For approximate nearest-neighbour search with in-memory performance:

```csharp
options.Schema.For<Document>()
    .HnswIndex(d => d.Embedding, dimension: 1536, distance: Search.Distance.Cosine);
```

Available distance functions: `COSINE`, `EUCLIDEAN`, `MANHATTAN`, `MINKOWSKI`, `HAMMING`, `JACCARD`, `INNER_PRODUCT`.

### DiskANN Vector Index (SurrealDB 3.1+)

For very large embedding sets that exceed available RAM:

```csharp
options.Schema.For<Document>()
    .DiskannIndex(
        d => d.Embedding,
        dimension: 1536,
        vectorType: "F32",
        distance: Search.Distance.Cosine,
        degree: 64,
        lBuild: 100,
        alpha: 1.2);
```

### Geo-Spatial Index

Marker index for geometry properties (btree under the hood, with geo post-filtering):

```csharp
options.Schema.For<Venue>()
    .SpatialIndex(v => v.Location);
```

### Hybrid Search Setup

Convenience method that configures both full-text and HNSW indexes:

```csharp
options.Schema.For<Document>()
    .HybridSearch(
        textFields: [("Title", 0.3), ("Body", 0.7)],
        vectorField: d => d.Embedding,
        dimension: 1536,
        analyzer: "english");
```

### Custom Field Definitions

Emit explicit `DEFINE FIELD` statements with type, default, and constraints:

```csharp
options.Schema.For<User>()
    .Field("email", f =>
    {
        f.FieldType = "string";
        f.DefaultValue = "'unknown@example.com'";
        f.AssertExpression = "string::is::email($value)";
    });
```

### Computed / Expression-Based Indexes

```csharp
options.Schema.For<User>()
    .ComputedIndex(u => new { u.FirstName, u.LastName }, cfg =>
    {
        cfg.Method = ComputedIndexOptions.IndexMethod.BTree;
        cfg.Casing = ComputedIndexOptions.IndexCasing.Lower;
    });
```

## Fluent Query Syntax

All queries start from a session and use the fluent `Query<T>()` builder.

### Basic Filtering

```csharp
var adults = await session.Query<User>()
    .Where(u => u.Age >= 18)
    .ToListAsync();
```

### AND / OR Queries

Combine conditions with C# `&&` and `||`:

```csharp
var results = await session.Query<User>()
    .Where(u => (u.Age >= 21 && u.Status == "active")
             || u.Role == "admin")
    .ToListAsync();
```

### Where with Multiple Conditions

```csharp
var filtered = await session.Query<Order>()
    .Where(o => o.Total > 100
             && o.CreatedAt >= startDate
             && o.CreatedAt <= endDate
             && o.Status != "cancelled")
    .ToListAsync();
```

String matching:

```csharp
var searched = await session.Query<User>()
    .Where(u => u.Name.StartsWith("A") && u.Email.EndsWith("@example.com"))
    .ToListAsync();
```

Collection membership:

```csharp
var team = await session.Query<User>()
    .Where(u => teamIds.Contains(u.Id))
    .ToListAsync();
```

### OrderBy / ThenBy

```csharp
var sorted = await session.Query<User>()
    .Where(u => u.Department == "Engineering")
    .OrderBy(u => u.LastName)
    .ThenByDescending(u => u.FirstName)
    .ToListAsync();
```

### Skip / Take Pagination

```csharp
var page2 = await session.Query<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.Name)
    .Skip(20)
    .Take(10)
    .ToListAsync();
```

### Select Projections

Project to DTOs or anonymous types for wire-efficient queries:

```csharp
var summaries = await session.Query<User>()
    .Where(u => u.Department == "Engineering")
    .Select(u => new UserSummary
    {
        Id = u.Id,
        FullName = u.FirstName + " " + u.LastName,
        Email = u.Email,
        Role = u.Role
    })
    .ToListAsync();
```

### First / FirstOrDefault

```csharp
var user = await session.Query<User>()
    .FirstOrDefaultAsync(u => u.Email == "alice@example.com");

// Throws if no match
var admin = await session.Query<User>()
    .FirstAsync(u => u.Role == "admin");
```

### Single / SingleOrDefault

```csharp
// Throws if zero or more than one match
var exact = await session.Query<User>()
    .SingleAsync(u => u.Id == userId);

// Returns null if no match, throws if multiple
var maybe = await session.Query<User>()
    .SingleOrDefaultAsync(u => u.Email == email);
```

### Count, Any, All

```csharp
var total = await session.Query<User>().CountAsync();
var active = await session.Query<User>().CountAsync(u => u.IsActive);
var hasPremium = await session.Query<User>().AnyAsync(u => u.Tier == "premium");
var allVerified = await session.Query<User>().AllAsync(u => u.IsEmailVerified);
```

### Sum, Average, Min, Max

```csharp
var totalRevenue = await session.Query<Order>()
    .SumAsync(o => o.Total);

var avgRating = await session.Query<Review>()
    .AverageAsync(r => r.Score);

var minPrice = await session.Query<Product>()
    .MinAsync(p => p.Price);

var maxSalary = await session.Query<Employee>()
    .MaxAsync(e => e.Salary);
```

### GroupBy Aggregations

```csharp
var deptStats = await session.Query<Employee>()
    .GroupBy(e => e.Department)
    .Select(g => new
    {
        Department = g.Key,
        Count = g.Count(),
        AvgSalary = g.Average(e => e.Salary),
        MaxSalary = g.Max(e => e.Salary)
    })
    .ToListAsync();
```

### Distinct

```csharp
var cities = await session.Query<User>()
    .Select(u => u.City)
    .Distinct()
    .ToListAsync();
```

## Raw SQL Fallback

When the LINQ provider cannot express a query, fall back to raw SurrealQL:

```csharp
var results = await session.RawQueryAsync<User>(
    "SELECT * FROM user WHERE age > $min AND geo::DISTANCE(location, $center) < $radius " +
    "ORDER BY age DESC LIMIT 20",
    new { min = 18, center = new { lat = 40.7128, lon = -74.0060 }, radius = 50000 });

// Raw query with type deserialization
var json = await session.RawQueryAsync<dynamic>(
    "SELECT count() AS total, math::mean(age) AS avg_age FROM user GROUP BY department");
```

Use `ToDebugString()` to inspect generated SurrealQL for any LINQ query:

```csharp
var (surrealql, parameters) = query.ToDebugString();
Console.WriteLine(surrealql); // SELECT ... FROM user WHERE ...
```

## Compiled Queries for Hot Paths

Avoid re-compiling the same query shape repeatedly in high-throughput paths by subclassing `CompiledQuery<T>`:

```csharp
public class UsersByCity : CompiledQuery<User>
{
    public string City { get; set; }

    public override Expression<Func<IQueryable<User>, IQueryable<User>>> Query()
        => q => q.Where(u => u.City == City);
}

var nycUsers = await session.QueryAsync(new UsersByCity { City = "NYC" });
```
