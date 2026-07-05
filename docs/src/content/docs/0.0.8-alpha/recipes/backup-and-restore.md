---
title: Backup and Restore
description: Export and import your SurrealDB data
slug: 0.0.8-alpha/recipes/backup-and-restore
---

## Goal

Export all data to a file and restore it later — useful for migrations, backups, and CI setup.

## Code

```csharp
using AeroDB;

var store = DocumentStore.For(cfg =>
    cfg.Connection("ws://localhost:8000/rpc"));

await using var session = store.LightweightSession();

// ── EXPORT ────────────────────────────────────────────────

// Export the entire database as SurrealQL statements
var exportResult = await session.ExecuteAsync(
    "RETURN SELECT * FROM OMIT $auth SCOPE 2147483647;"
);

// The result is a list of all records — write to a file
var exportJson = System.Text.Json.JsonSerializer.Serialize(exportResult);
await File.WriteAllTextAsync("backup.json", exportJson);

// ── IMPORT ────────────────────────────────────────────────

// Read the backup and replay the INSERT statements
var backup = System.Text.Json.JsonSerializer
    .Deserialize<List<Dictionary<string, object>>>(File.ReadAllText("backup.json"));

foreach (var record in backup!)
{
    var id = record["id"];
    await session.ExecuteAsync(
        $"CREATE {id} CONTENT $content",
        new Dictionary<string, object?> { ["content"] = record }
    );
}
await session.SaveChangesAsync();
```

## Explanation

* `session.ExecuteAsync()` runs arbitrary SurrealQL queries directly.
* `SELECT * FROM` with `OMIT $auth SCOPE 2147483647` exports everything without auth filtering.
* For production, use SurrealDB's built-in `EXPORT` and `IMPORT` CLI commands for full binary backups.
* The in-process export shown here is suitable for test data, seeding, and small datasets.

## Production Backup

```bash
# SurrealDB CLI export (recommended for production)
surreal export --conn ws://localhost:8000 --user root --pass root \
  --ns production --db myapp export.sql

# SurrealDB CLI import
surreal import --conn ws://localhost:8000 --user root --pass root \
  --ns production --db myapp export.sql
```

## See Also

* [Raw SurrealQL](/0.0.8-alpha/guides/surrealql/)
* [SurrealDB CLI docs](https://surrealdb.com/docs/cli)
