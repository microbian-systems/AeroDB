---
title: Raw SurrealQL
description: Execute raw SurrealQL queries
---

While LINQ covers most scenarios, raw SurrealQL is necessary for administrative commands, multi-statement scripts, and operations outside the LINQ expression tree.

## When to Use Raw SurrealQL vs LINQ

| Scenario | Approach |
|----------|----------|
| CRUD on entities | LINQ (`session.Query<T>()`) |
| Complex graph traversals | LINQ (`session.Graph<T>()`) |
| Schema operations | Raw SurrealQL |
| Custom functions / MATCH | Raw SurrealQL |
| Multi-statement transactions | Raw SurrealQL |
| Admin / INFO commands | Raw SurrealQL |

## RawQueryAsync for Read Queries

```csharp
var results = await session.RawQueryAsync<User>(
    "SELECT * FROM user WHERE age > $minAge ORDER BY name LIMIT 10",
    new { minAge = 21 });
```

Typed overload with explicit mapping:

```csharp
var dtos = await session.RawQueryAsync<UserSummary>(
    "SELECT id, name, email FROM user WHERE department = $dept",
    new { dept = "Engineering" });
```

## ExecuteSqlAsync for Schema / DDL

```csharp
await session.ExecuteSqlAsync(@"
    DEFINE TABLE user SCHEMAFULL;
    DEFINE FIELD email ON user TYPE string;
    DEFINE INDEX email_unique ON user COLUMNS email UNIQUE;
");
```

## Parameter Binding

Use anonymous objects or a dictionary for parameters. Values are escaped automatically:

```csharp
// Anonymous object
await session.RawQueryAsync<User>(
    "SELECT * FROM user WHERE status = $status AND age > $minAge",
    new { status = "active", minAge = 18 });

// Dictionary
var parameters = new Dictionary<string, object>
{
    ["status"] = "active",
    ["tags"] = new[] { "premium", "beta" }
};
await session.RawQueryAsync<User>(
    "SELECT * FROM user WHERE status = $status AND $tags INSIDE tags",
    parameters);
```

## Multi-Statement Scripts

Separate statements with `;` and use `*` returns:

```csharp
var results = await session.RawQueryAsync<object[]>(@"
    LET $user = (CREATE user SET name = 'Alice');
    CREATE profile SET user_id = $user.id, bio = 'Engineer';
    SELECT * FROM user WHERE id = $user.id;
");
```

## Administrative Commands

```csharp
// DEFINE
await session.ExecuteSqlAsync("DEFINE TABLE event SCHEMAFULL");
await session.ExecuteSqlAsync("DEFINE INDEX idx_name ON user FIELDS name");
await session.ExecuteSqlAsync("DEFINE ANALYZER english TOKENIZERS class");

// REMOVE
await session.ExecuteSqlAsync("REMOVE TABLE temp_data");
await session.ExecuteSqlAsync("REMOVE INDEX idx_old ON user");

// INFO
var schema = await session.RawQueryAsync<SchemaInfo>("INFO FOR DB");
var tableInfo = await session.RawQueryAsync<TableInfo>("INFO FOR TABLE user");
```

## Typed Results with Generic Overloads

```csharp
// Single type
var users = await session.RawQueryAsync<User>("SELECT * FROM user");

// Multiple result sets
var results = await session.RawQueryAsync<object[]>(script);
var users = results[0] as List<User>;
var profiles = results[1] as List<Profile>;

// With custom deserialization
var json = await session.RawQueryAsync<JsonElement>("SELECT * FROM user");
```

Always prefer LINQ for application queries and reserve raw SurrealQL for schema management, administrative tasks, and operations the LINQ provider cannot express.
