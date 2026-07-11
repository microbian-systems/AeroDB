---
title: Configuration
description: Configure AeroDB for your application — connections, schemas, indexes, multi-tenancy, and store initialization
---

AeroDB is configured through the `StoreOptions` class, passed to the `Documents.For()` factory or the `AddAeroDB()` DI extension. This guide covers every configuration surface.

---

## Namespace & Database

Every AeroDB store connects to a SurrealDB namespace and database. These are the top-level organizational units in SurrealDB.

```csharp
var store = Documents.For(opts =>
{
    opts.Namespace = "myapp";
    opts.Database  = "production";
});
```

A shorthand using `Connection()` also sets the endpoint:

```csharp
opts.Connection(
    endpoint: "http://localhost:8000",
    ns:       "myapp",
    db:       "production"
);
```

Both properties default to `"test"` if not set, which is useful for prototyping but should always be overridden in production.

### Connection Properties

| Property       | Default                | Description                                |
|----------------|------------------------|--------------------------------------------|
| `Endpoint`     | `http://localhost:8000`| SurrealDB server URL                       |
| `Namespace`    | `null`                 | SurrealDB namespace (defaults to `"test"`) |
| `Database`     | `null`                 | SurrealDB database (defaults to `"test"`)  |
| `Username`     | `null`                 | Username for authentication                |
| `Password`     | `null`                 | Password for authentication                |
| `Token`        | `null`                 | JWT/ bearer token for authentication       |

---

## Connection Options

### Local (default)

```csharp
opts.Endpoint = "http://localhost:8000";
```

### Remote SurrealDB Server

```csharp
opts.Connection(
    endpoint: "https://db.example.com:8000",
    ns:       "myapp",
    db:       "production",
    username: "admin",
    password: "secure-password"
);
```

### SurrealDB Cloud

```csharp
opts.Connection(
    endpoint: "https://cloud-abc123.surrealdb.cloud:8000",
    ns:       "myapp",
    db:       "production",
    username: "root",
    password: "root"  // Provided in cloud console
);
```

### JWT / Token Authentication

```csharp
opts.Connection(
    endpoint: "https://db.example.com:8000",
    ns:       "myapp",
    db:       "production",
    token:    "eyJhbGciOiJIUzI1NiIs..."
);
```

### Custom Client Factory

For full control over the SurrealDB client, provide a factory delegate:

```csharp
opts.ClientFactory = () =>
{
    var surrealOpts = new SurrealDbOptionsBuilder()
        .WithEndpoint("https://db.example.com:8000")
        .WithNamespace("myapp")
        .WithDatabase("production")
        .WithUsername("admin")
        .WithPassword("password")
        .Build();

    return new SurrealDbClient(surrealOpts);
};
```

### Multiple Endpoints / Read Replicas

Configure multiple database endpoints for read-replica or multi-host scenarios:

```csharp
opts.AddDatabaseEndpoint("http://replica-1:8000", ep => ep.Role = "replica");
opts.AddDatabaseEndpoint("http://replica-2:8000", ep => ep.Role = "replica");
opts.ReadPreference = ReadPreference.Secondary; // Route queries to replicas
```

---

## Schema Configuration

Document schemas are defined via the `Schema.For<T>()` fluent API on `StoreOptions`. This tells AeroDB which tables to create, what indexes to build, and how to map your C# types.

### Basic Schema

```csharp
opts.Schema.For<User>()
    .Identity(u => u.Id)          // Primary key property
    .Index(u => u.Email, c => c.IsUnique());
```

### Multi-Field Indexes

Use anonymous types for composite indexes:

```csharp
opts.Schema.For<User>()
    .Index(u => new { u.FirstName, u.LastName });
```

Configure additional options with the `Action<IndexOptions>` delegate:

```csharp
opts.Schema.For<User>()
    .Index(u => u.Email, c =>
    {
        c.IsUnique();
        c.WithName("idx_user_email");
    });
```

### BTree Index (explicit)

```csharp
opts.Schema.For<Product>()
    .BTreeIndex(p => p.Sku);
```

This is equivalent to the default `Index()` — SurrealDB's default index type is btree.

### Custom Field Definitions

Define field types, defaults, assertions, and permissions:

```csharp
opts.Schema.For<User>()
    .Field("email", f =>
    {
        f.FieldType = "string";
        f.DefaultValue = "'unknown@example.com'";
        f.AssertExpression = "string::is::email($value)";
    })
    .Field("role", f =>
    {
        f.FieldType = "string";
        f.DefaultValue = "'user'";
        f.Permissions = "WHERE $auth.role = 'admin'";
    });
```

### Foreign Key Metadata

Declare foreign key relationships (informational — SurrealDB uses graph edges for referential integrity):

```csharp
opts.Schema.For<Order>()
    .ForeignKey<User>(o => o.UserId, fk => fk.OnDelete = CascadeAction.SetNull);
```

### Schema Per-Type (Multi-Database)

Map a document type to a **different SurrealDB database** using `.Schema()`.

```csharp
opts.Schema.For<AnalyticsEvent>()
    .Schema("analytics_db");     // Stored in the "analytics_db" database

opts.Schema.For<AuditLog>()
    .Schema("audit_db");         // Stored in the "audit_db" database

// Document types without .Schema() go to the default Database
opts.Schema.For<User>();         // Stored in opts.Database
```

When `InitializeAsync` runs, AeroDB creates each schema database (if `AutoCreateDatabases` is `true`) and applies the relevant indexes for each type.

### Enable Auto-Create Databases

```csharp
opts.Schema.AutoCreateDatabases = true;
```

When enabled, AeroDB calls `DEFINE DATABASE IF NOT EXISTS` for each schema name encountered during initialization. Requires the connection user to have adequate privileges.

### Edge / Relation Schemas

Configure SurrealDB `RELATION` tables with typed IN/OUT constraints:

```csharp
opts.Schema.Edge<Membership, User, Team>(edge =>
{
    edge.TableName = "membership";
});
```

This generates a `DEFINE TABLE membership TYPE RELATION ...` statement with `FROM user` and `TO team` type constraints.

---

## Schema Modes

AeroDB supports two SurrealDB schema modes per document type:

| Mode         | SurrealQL        | Behavior                                                       |
|--------------|------------------|----------------------------------------------------------------|
| `Strict`     | `SCHEMAFULL`     | Only explicitly defined fields are permitted; extras are rejected |
| `Flexible`   | `SCHEMALESS`     | Defined fields are typed/validated, but extra fields are allowed  |

### Strict Mode (default)

```csharp
opts.Schema.For<User>()
    .SetSchemaMode(SchemaMode.Strict);
```

This is the default. AeroDB generates `DEFINE TABLE user SCHEMAFULL` and `DEFINE FIELD` statements for every discovered property. Documents with undeclared fields will be rejected by SurrealDB.

### Flexible Mode

```csharp
opts.Schema.For<User>()
    .SetSchemaMode(SchemaMode.Flexible);
```

Useful when you need schema flexibility — for example, when storing dynamic or user-defined fields alongside typed properties.

---

## Index Types

AeroDB supports all SurrealDB index types through a fluent configuration API.

### Standard (BTree)

The default index type. Suitable for equality and range queries.

```csharp
opts.Schema.For<Product>()
    .Index(p => p.Sku);
    // Generates: DEFINE INDEX idx_product_sku ON product FIELDS sku
```

### Unique Index

```csharp
opts.Schema.For<User>()
    .Index(u => u.Email, c => c.IsUnique());
    // Generates: DEFINE INDEX uidx_user_email ON user FIELDS email UNIQUE
```

Shorthand helper available:

```csharp
opts.Schema.For<User>()
    .UniqueIndex(u => u.Email);
```

### Composite (Multi-Column) Index

```csharp
opts.Schema.For<Order>()
    .Index(o => new { o.Status, o.CreatedAt });
    // Generates: DEFINE INDEX idx_order_status_created_at ON order FIELDS status, created_at
```

### Full-Text Search Index

Requires an [analyzer](https://surrealdb.com/docs/surrealql/statements/define/analyzer) to be defined (or a built-in like `"english"`, `"simple"`, `"swedish"`, etc.).

```csharp
opts.Schema.For<Article>()
    .FullTextIndex(a => a.Body, "english");
    // Generates: DEFINE INDEX ft_article_body ON article FIELDS body FULLTEXT ANALYZER english
```

With optional BM25 scoring parameters:

```csharp
opts.Schema.For<Article>()
    .FullTextIndex(a => a.Title, "english", bm25: (k1: 1.2, b: 0.75));
```

Multi-field full-text index:

```csharp
opts.Schema.For<Article>()
    .FullTextIndex("english",
        a => a.Title,
        a => a.Body);
    // Generates: DEFINE INDEX ft_article_title_body ON article FIELDS title, body FULLTEXT ANALYZER english
```

### HNSW Vector Index

For approximate nearest-neighbor search on vector embeddings. Ideal for large datasets where speed matters more than exact results.

```csharp
opts.Schema.For<Document>()
    .HnswIndex(
        d => d.Embedding,
        dimension: 1536,          // OpenAI ada-002 dimensionality
        distance: "COSINE"         // COSINE (default), EUCLIDEAN, or MANHATTAN
    );
    // Generates: DEFINE INDEX hnsw_document_embedding ON document FIELDS embedding HNSW DIMENSION 1536 DIST COSINE
```

The vector property should be a `List<float>`, `float[]`, or `ReadOnlyMemory<float>`.

### DISKANN Vector Index

Disk-based approximate nearest-neighbor search for very large embedding sets that exceed available RAM. Available since SurrealDB 3.1.

```csharp
opts.Schema.For<Document>()
    .DiskannIndex(
        d => d.Embedding,
        dimension: 1536,
        vectorType: "F32",          // F32 (default), F16, I8, or U8
        distance: "COSINE",         // COSINE, EUCLIDEAN, INNER_PRODUCT, COSINE_NORMALIZED
        degree: 64,                 // Graph degree (default 64)
        lBuild: 100,                // Construction search-list size (default 100)
        alpha: 1.2                  // Pruning parameter (default 1.2)
    );
    // Generates: DEFINE INDEX diskann_document_embedding ON document FIELDS embedding DISKANN DIMENSION 1536 TYPE F32 DIST COSINE
```

### Geo-Spatial Index

Marks a geometry property for spatial query optimization. Under the hood SurrealDB uses a btree; spatial queries use bounding-box pre-filters with `geo::DISTANCE` post-filters.

```csharp
opts.Schema.For<Place>()
    .SpatialIndex(p => p.Location);
    // Generates: DEFINE INDEX geo_place_location ON place FIELDS location
```

The property type should be `GeometryPoint` or `GeometryPolygon` from the `AeroDB.Geometry` namespace.

### Hybrid Search (Full-Text + Vector)

Convenience method that creates both full-text and HNSW indexes for hybrid retrieval-augmented generation (RAG) workloads.

```csharp
opts.Schema.For<Document>()
    .HybridSearch(
        textFields: [
            (FieldName: "title", Weight: 1.0),
            (FieldName: "body",  Weight: 0.7)
        ],
        vectorField: d => d.Embedding,
        dimension: 768,
        analyzer: "english",
        distance: "COSINE"
    );
```

### Computed / Expression-Based Indexes

Define indexes on computed expressions:

```csharp
opts.Schema.For<User>()
    .ComputedIndex(u => u.Email, c =>
    {
        c.Method = ComputedIndexOptions.IndexMethod.BTree;
        c.Casing = ComputedIndexOptions.IndexCasing.Lower;
        c.Order  = ComputedIndexOptions.SortOrder.Asc;
    });
```

### Custom Analyzers

Define custom SurrealDB analyzers before they are referenced by full-text indexes:

```csharp
opts.Schema.Analyzers.Define("my_analyzer", a =>
{
    a.Filter = ["lowercase", "snowball(english)"];
    a.Tokenizer = "classic";
});
```

### Ignoring Indexes

Prevent specific indexes from being auto-created (useful when the index already exists):

```csharp
opts.Schema.For<User>()
    .IgnoreIndex("idx_user_email");
```

---

## Multi-Tenancy

AeroDB supports three multi-tenancy strategies.

### Single Tenant (default)

No tenant isolation — all data lives in one namespace/database.

```csharp
opts.TenancyStyle = TenancyStyle.Single; // default
```

### Conjoined Tenancy

All tenants share the same database, but each document is tagged with a `tenant_id` field. Queries automatically filter by tenant.

```csharp
// Global: mark all documents as multi-tenanted
opts.AllDocumentsAreMultiTenanted();

// Or per-type
opts.Schema.For<Order>().MultiTenanted();

// Optional: set a default tenant for sessions
opts.DefaultTenantId = "tenant-alpha";
```

Documents must include a `TenantId` property or implement the tenancy interface. When `DefaultTenantId` is set, all sessions use it unless overridden via `session.SetTenant()`.

### Database-Per-Tenant

Each tenant gets its own SurrealDB database. The database name is derived from the namespace and tenant ID (`{namespace}_{tenantId}`).

```csharp
opts.MultiTenantedDatabases();
// Sets opts.TenancyStyle = TenancyStyle.DatabasePerTenant
```

Creating a session for a specific tenant:

```csharp
// Using WithTenant on the store
await using var session = store.WithTenant("acme-corp").LightweightSessionAsync();

// Using SessionOptions
var session2 = await store.OpenSessionAsync(new SessionOptions
{
    TenantId = "acme-corp"
});
```

In DatabasePerTenant mode, `InitializeAsync` defers database creation to the first session open, so no tenant database is created until it is first accessed.

### Tenant ID Style

Control case sensitivity of tenant IDs:

```csharp
opts.TenantIdStyle = TenantIdStyle.CaseInsensitive; // default: CaseSensitive
```

---

## Store Initialization

Call `InitializeAsync` after configuration, typically in your application startup.

```csharp
var store = Documents.For(opts => { /* ... */ });
await store.InitializeAsync();
```

What happens during initialization:

1. **Connection** — Connects to SurrealDB and authenticates
2. **Configurators** — Applies `IConfigureAeroDB` and `IAsyncConfigureAeroDB` modules (both manual registrations and DI-discovered)
3. **Policies** — Applies global document policies to all mappings
4. **Analyzers** — Creates custom `DEFINE ANALYZER` statements
5. **Tables & Indexes** — Creates `DEFINE TABLE` and `DEFINE INDEX` for every registered mapping (including per-schema databases)
6. **Edge Schemas** — Creates `DEFINE TABLE ... TYPE RELATION` for edge mappings
7. **Event Store** — Creates event sourcing tables if `opts.Events.Enabled = true`
8. **Triggers** — Creates SurrealDB native `DEFINE EVENT` triggers
9. **Functions** — Creates `DEFINE FUNCTION` for user-defined functions
10. **Views** — Creates `DEFINE TABLE ... AS SELECT ...` materialized views
11. **Auth** — Creates `DEFINE ACCESS`, `DEFINE TOKEN`, and `DEFINE SCOPE` definitions
12. **Projections** — Ensures projection state tables and optionally rebuilds projections on startup
13. **Seed Data** — Runs `IInitialData` seeders in order

### Async Initialization

`InitializeAsync` is thread-safe and idempotent. If multiple threads attempt to initialize concurrently, only the first succeeds; others wait or return immediately.

---

## Advanced Options

### Optimistic Concurrency

Enable optimistic concurrency globally or per-type to prevent lost updates:

```csharp
// Global
opts.UseOptimisticConcurrency = true;
opts.AllDocumentsEnforceOptimisticConcurrency();

// Per-type
opts.Schema.For<User>().UseOptimisticConcurrency = true;
```

Documents must have a version property (either implementing `IVersioned` or decorated with `[Version]`).

### Soft Delete

Enable soft-delete globally or per-type:

```csharp
// All documents
opts.AllDocumentsSoftDeleted();

// Per-type
opts.Schema.For<Invoice>().SoftDeleted();

// Query auto-filtering (default: true)
opts.SoftDeleteEnabled = true;
```

Soft-deleted documents get `Deleted = true` and `DeletedAt` timestamps. Queries automatically filter them out unless `opts.SoftDeleteEnabled = false`.

### Serialization

Customize JSON serialization:

```csharp
opts.SerializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

// Or use the convenience method:
opts.ConfigureSerializer(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
```

### Polly Resilience

Configure the Polly resilience pipeline for SurrealDB client operations:

```csharp
opts.ConfigurePolly(pipeline =>
{
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromMilliseconds(100)
    });
});
```

### OpenTelemetry

```csharp
opts.OpenTelemetry = new OpenTelemetryOptions
{
    IncludeRequestBody = true,
    IncludeResponseBody = false
};
```

### Logging

```csharp
opts.LoggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
});
opts.MinimumLogLevel = LogLevel.Information;
```

### Command Timeout

```csharp
opts.CommandTimeout = 30; // seconds. null = no timeout.
```

### Update Batch Size

```csharp
opts.UpdateBatchSize = 500; // Max operations per SaveChangesAsync batch
```

---

## ASP.NET Core DI Configuration

The recommended way to integrate with ASP.NET Core:

```csharp
builder.Services.AddAeroDB(opts =>
{
    opts.Connection(
        "http://localhost:8000",
        builder.Configuration["SurrealDB:Namespace"],
        builder.Configuration["SurrealDB:Database"],
        username: builder.Configuration["SurrealDB:Username"],
        password: builder.Configuration["SurrealDB:Password"]
    );

    opts.Schema.For<User>()
        .UniqueIndex(u => u.Email);

    opts.Schema.For<Product>()
        .Index(p => p.Sku, c => c.IsUnique())
        .FullTextIndex(p => p.Description, "english");
});

// The store automatically initializes on first use.
// To eagerly initialize at startup:
var app = builder.Build();
var store = app.Services.GetRequiredService<IDocumentStore>();
await store.InitializeAsync();
```

### Configuration Modules

For modular configuration, implement `IConfigureAeroDB`:

```csharp
public class UserModuleConfig : IConfigureAeroDB
{
    public void Configure(IServiceProvider? services, StoreOptions options)
    {
        options.Schema.For<User>()
            .UniqueIndex(u => u.Email);
    }
}

// Register in DI
builder.Services.ConfigureAeroDB<UserModuleConfig>();
```

---

## Complete Example

Putting it all together:

```csharp
var store = Documents.For(opts =>
{
    // Connection
    opts.Connection("https://db.example.com:8000", "myapp", "production",
        username: "admin", password: "s3cret");

    // Schema mode
    opts.Schema.For<User>()
        .Identity(u => u.Id)
        .UniqueIndex(u => u.Email)
        .Index(u => new { u.FirstName, u.LastName })
        .FullTextIndex(u => u.Bio, "english")
        .SetSchemaMode(SchemaMode.Strict);

    opts.Schema.For<Product>()
        .BTreeIndex(p => p.Sku, c => c.IsUnique())
        .HnswIndex(p => p.VectorEmbedding, dimension: 384);

    opts.Schema.For<AnalyticsEvent>()
        .Schema("analytics")   // separate database
        .Index(e => new { e.Category, e.Timestamp });

    // Multi-tenancy
    opts.Schema.For<Order>().MultiTenanted();

    // Soft delete
    opts.Schema.For<Invoice>().SoftDeleted();

    // Performance
    opts.UpdateBatchSize = 1000;
    opts.CommandTimeout = 60;

    // Resilience
    opts.ConfigurePolly(p => p.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3
    }));
});

await store.InitializeAsync();
```

---

## See Also

- [Quick Start](/docs/getting-started/quick-start)
- [Document Schema](/docs/schema/overview)
- [Multi-Tenancy](/docs/multi-tenancy/overview)
- [Event Sourcing](/docs/events/overview)
- [LINQ Querying](/docs/querying/linq)
