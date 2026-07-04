# Changelog

## 0.0.7-alpha (2025-06-26)

### Entity Types as First-Class Citizens

Entity types (`Entity<TId>`, `EntitySnowlake`, `EntityString`, `EntityInt`, `EntityGuid`) are now fully supported alongside Record types throughout the API.

#### Constraints Relaxed

`where T : IRecord` → `where T : class` on:

- `DocumentMapping<T>` — schema mapping constructor
- `Schema.For<T>()` — schema registration
- `SchemaManager.EnsureDocumentSchemaAsync<T>()` — schema provisioning
- `ISurrealDbQueryable.IncludeReverse<T, TChild>()` — reverse includes
- `ISurrealDbQueryable.FilterInclude<T, TChild>()` — filter includes

A runtime guard rejects non-document types (e.g., `Schema.For<string>()`) with a clear error message.

#### Source Generators

- **DaliDocumentGenerator** extended — detects `Entity<TId>` subclasses and emits `ITypeMetadata<T>` with entity-aware `GetRecordId()` that returns the typed `Id` via `.ToString()`
- **DaliEntityShimGenerator** — new incremental generator that produces CBOR-compatible `Record` shims for Entity types, enabling `LoadAsync<T>()` via existing CBOR deserialization pipeline

#### Bugfix: IncludeReverse with Entity Types

Fixed a SurrealQL generation bug where reverse-include subqueries hardcoded `id` (RecordId) instead of using `Id` (typed entity field). The subquery now selects the correct field based on parent type:

- Record types → `SELECT VALUE id FROM $main`
- Entity types → `SELECT VALUE Id FROM $main`

Applied in all three query paths: `ToListAsync`, `FirstOrDefaultAsync`, `SingleOrDefaultAsync`.

#### Performance

- **BulkInsert ~40% slower for Entity types** — accepted cost of Snowflake ID generation and SurrealDB explicit-ID parsing
- **BulkInsert benchmarks fixed** — data generation moved from measured `[Benchmark]` method to `[IterationSetup]`
- **Parent ID field pre-computed** — `IncludeSpec.ParentIdField` computed at build time instead of per-query reflection

#### Testing (78 tests, 0 failures)

- 23 IEntityTests — CRUD + LoadAsync for all TId variants (long, string, int, Guid)
- 11 EntitySchemaTests — schema registration, indices, runtime guard
- 8 EntityMetadataTests — source-gen metadata correctness for all TId types
- 8 EntityIncludeTests — IncludeReverse for 4 FK types + FirstOrDefault/SingleOrDefault
- 1 EntityQueryableExtensionTests — compile-time IncludeReverse availability
- 27 FetchIncludeIntegrationTests — no regressions in Record-type include path

#### Documentation

- `docs/entity-feature-matrix.md` — complete table of Record vs Entity capabilities
- Updated IncludeReverse XML doc — reflects `[id|Id]` field selection

### Known Limitations

- Entity types cannot participate in graph edge relations (`Edge<,,>`) — use Record types
- Entity types don't support polymorphic dispatch (`HierarchyFor<TBase>()`) — Record-only
- Forward `Include` does not support Entity child types — requires `Record`

### Publishing

- NuGet: `Dali` `Dali.SourceGenerators` `Dali.EntityFrameworkCore` `Dali.ML` `Dali.WolverineFx`
- Versioning: `0.0.7-alpha` — prerelease on NuGet.org
