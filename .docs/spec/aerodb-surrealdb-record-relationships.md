# AeroDB SurrealDB Record Relationship Implementation Spec

## Purpose

Implement provider-neutral record relationship mapping in AeroDB, with a SurrealDB provider implementation that uses SurrealDB record links and arrays of record links for ordinary object/document relationships.

This spec intentionally separates ordinary record relationships from graph relationships.

- **Record relationships**: EF/Marten-style object references, stored as direct record IDs or arrays of record IDs.
- **Graph relationships**: SurrealDB `RELATE` edge records, used for graph traversal and edge metadata.

Do not collapse these into one concept internally.

---

## Terminology

### AeroDB public terminology

Preferred terms:

- `HasOne(...)` for a single related record.
- `HasMany(...)` for a collection of related records.
- `Include(...)` for query-time eager loading/hydration.

Avoid using `Relate(...)` for ordinary record relationships if AeroDB already uses that name for graph edge relationships. In SurrealDB, `RELATE` specifically means creating graph edges.

### SurrealDB terminology

- A single ordinary reference is a **record link**.
- A collection of ordinary references is an **array of record links**.
- A graph edge is created with `RELATE` and is a separate relationship model.

---

## Relationship Models

## Relationship Metadata Pipeline

Relationship discovery and query translation must use one shared metadata model, not separate query-time heuristics.

### Compile-time candidate discovery

Source generation may emit shape-only relationship candidates:

```text
Order.CustomerId -> Customer.Id
Order.Customer -> Customer
Customer.AddressId -> Address.Id
```

Generated candidates are an optimization and diagnostics layer. They are not authoritative because runtime schema configuration can still override table names, field names, naming policy, identity members, and module composition.

The generator should emit a module-initialized `GeneratedRelationshipCatalog` that registers `RelationshipCandidate` values with runtime metadata. Candidates remain shape-only and may be ignored by startup finalization if the composed schema cannot validate the source type, target type, identity member, or scalar FK type compatibility.

### Startup finalization

`SchemaOptions.ResolveRelationships()` / schema finalization runs after all `Schema.For<T>()` configuration and before DDL generation or query planning. It resolves:

- identity members first
- explicit fluent relationships
- generated or reflection-discovered convention candidates
- naming policy, table names, and field names
- ambiguity and type-compatibility checks

Finalized relationships are exposed through normal relationship metadata so `Link`, `Join`, `Include`, `IncludeReverse`, `ThenInclude`, materialization, tooling, and compiled queries can all consume the same model.

Compiled query planning must be schema-aware. Interface-based compiled queries should build plans with the session/store schema so relationship fluent chains such as `Link<T>()`, `Join<T>()`, `Include(...)`, and `ThenInclude(...)` resolve against finalized descriptors instead of query-time reflection fallbacks or mutable queryable state alone.

### Descriptor axes

Do not encode storage model and cardinality into one enum. They are independent axes:

```csharp
public enum RelationshipStorageKind
{
    ScalarForeignKey,
    RecordLink
}

public enum RelationshipCardinality
{
    One,
    Many
}
```

Requiredness/nullability and DDL constraints are separate concerns:

- `IsNullable`: the CLR/storage value can be absent, null, or `NONE`.
- `IsRequired`: schema/validation says the relationship must be present.
- `RelationshipConstraints`: DDL-only behavior such as `REFERENCE`, `ON DELETE`, and `UNIQUE`.

`RecordLinkArray` is represented by `StorageKind = RecordLink` plus `Cardinality = Many`; it should not require a separate long-term storage enum value.

### Ambiguity rules

Convention discovery must distinguish "no match" from "ambiguous match":

- Duplicate mapped target type names: hard startup/build diagnostic naming the full types and assemblies.
- Multiple FK-shaped members to the same target: valid; register separate descriptors keyed by source member.
- FK/identity type mismatch: not a relationship match; leave the source member as a scalar field.
- Unknown target identity: resolve identity first, then run relationship inference.
- Competing conventions for the same member: hard diagnostic, not first-registered-wins.

Explicit fluent configuration overrides convention-discovered relationships.

---

## 0. Scalar Foreign Key Relationship

Scalar FK relationships are AeroDB relationship metadata backed by ordinary scalar fields, not SurrealDB native record-link fields.

### C# model

```csharp
public sealed class Order
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
}

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
```

### Convention mapping

```text
Order.CustomerId -> Customer.Id
```

### Explicit mapping

```csharp
Schema.For<Order>()
    .HasOne<Customer>(x => x.CustomerId);
```

### Mapping metadata produced

```text
SourceType: Order
SourceTable: order
MemberName: CustomerId
StorageFieldName: customer_id
TargetType: Customer
TargetTable: customer
Kind/Cardinality: HasOne / One
StorageKind: ScalarForeignKey
Origin: Convention or Explicit
```

### DDL behavior

No SurrealDB `record<customer>` field is emitted for scalar FK relationships. The scalar field is treated as a normal field; relationship metadata is used by query translation, include loading, and materialization.

---

## 1. Single Record Relationship

### C# model

```csharp
public sealed class Order
{
    public long Id { get; set; }
    public Customer Customer { get; set; } = default!;
}

public sealed class Customer
{
    public long Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
}
```

### AeroDB schema mapping

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer);
```

### Mapping metadata produced

```text
SourceType: Order
SourceTable: order
MemberName: Customer
StorageFieldName: customer
TargetType: Customer
TargetTable: customer
Kind: HasOne
StorageModel: RecordLink
```

### SurrealQL schema generated for SCHEMAFULL

```sql
DEFINE TABLE customer SCHEMAFULL;
DEFINE TABLE order SCHEMAFULL;

DEFINE FIELD customer
ON TABLE order
TYPE record<customer>;
```

### SurrealQL record shape

```sql
CREATE customer:troy SET
    first_name = "Troy",
    last_name = "Robinson";

CREATE order:1 SET
    customer = customer:troy;
```

Stored conceptually as:

```json
{
  "id": "order:1",
  "customer": "customer:troy"
}
```

---

## 2. Many Record Relationship

### C# model

```csharp
public sealed class Order
{
    public long Id { get; set; }
    public List<Product> Products { get; set; } = new();
}

public sealed class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

### AeroDB schema mapping

```csharp
Schema.For<Order>()
    .HasMany(x => x.Products);
```

### Mapping metadata produced

```text
SourceType: Order
SourceTable: order
MemberName: Products
StorageFieldName: products
TargetType: Product
TargetTable: product
Kind: HasMany
StorageModel: RecordLinkArray
```

### SurrealQL schema generated for SCHEMAFULL

```sql
DEFINE TABLE product SCHEMAFULL;
DEFINE TABLE order SCHEMAFULL;

DEFINE FIELD name ON TABLE product TYPE string;
DEFINE FIELD price ON TABLE product TYPE decimal;

DEFINE FIELD products
ON TABLE order
TYPE array<record<product>>;
```

### SurrealQL record shape

```sql
CREATE product:keyboard SET
    name = "Keyboard",
    price = 99.99;

CREATE product:mouse SET
    name = "Mouse",
    price = 49.99;

CREATE order:1 SET
    products = [
        product:keyboard,
        product:mouse
    ];
```

Stored conceptually as:

```json
{
  "id": "order:1",
  "products": [
    "product:keyboard",
    "product:mouse"
  ]
}
```

This is **not** a graph. It is just an array of record references.

---

## Query-Time Behavior

## Projection through related record

### User query

```csharp
var result = await session.Query<Order>()
    .Where(x => x.Id == orderId)
    .Select(x => new
    {
        x.Id,
        x.Customer.FirstName,
        x.Customer.LastName
    })
    .FirstOrDefaultAsync();
```

### Required mapping

This only works if AeroDB has this mapping:

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer);
```

The LINQ provider should inspect the expression tree and resolve `Order.Customer` through mapping metadata. If `Customer` is not configured as a relationship, the query provider should reject the query with a clear error.

### SurrealQL translation

```sql
SELECT
    id,
    customer.first_name AS customer_first_name,
    customer.last_name AS customer_last_name
FROM order
WHERE id = $orderId
LIMIT 1;
```

A provider may choose aliases based on the projection shape.

---

## Projection through many related records

### User query

```csharp
var result = await session.Query<Order>()
    .Where(x => x.Id == orderId)
    .Select(x => new
    {
        x.Id,
        ProductNames = x.Products.Select(p => p.Name).ToList()
    })
    .FirstOrDefaultAsync();
```

### Required mapping

```csharp
Schema.For<Order>()
    .HasMany(x => x.Products);
```

### Possible SurrealQL translation

```sql
SELECT
    id,
    products.name AS productNames
FROM order
WHERE id = $orderId
LIMIT 1;
```

Alternative if full product records are needed:

```sql
SELECT *
FROM order
WHERE id = $orderId
FETCH products
LIMIT 1;
```

---

## Include Behavior

`Include(...)` should mean: hydrate the related object or collection on the returned root entity.

It should not mean: define a relationship.

In AeroDB, keep the existing distinction between provider-neutral eager hydration and SurrealDB-native fetch behavior:

- `Fetch(...)`: SurrealDB-specific `FETCH` clause.
- `Include(...)`: provider-neutral eager hydration API.

For SurrealDB record links, `Include(...)` may internally translate to `FETCH` when the relationship is represented as a direct record link or array of record links. Existing LET/subquery include behavior should remain available for reverse includes and typed-FK scenarios where `FETCH` is not the right primitive.

### Single include

```csharp
var order = await session.Query<Order>()
    .Include(x => x.Customer)
    .FirstOrDefaultAsync(x => x.Id == orderId);
```

SurrealQL:

```sql
SELECT *
FROM order
WHERE id = $orderId
FETCH customer
LIMIT 1;
```

### Many include

```csharp
var order = await session.Query<Order>()
    .Include(x => x.Products)
    .FirstOrDefaultAsync(x => x.Id == orderId);
```

SurrealQL:

```sql
SELECT *
FROM order
WHERE id = $orderId
FETCH products
LIMIT 1;
```

### Multiple includes

```csharp
var order = await session.Query<Order>()
    .Include(x => x.Customer)
    .Include(x => x.Products)
    .FirstOrDefaultAsync(x => x.Id == orderId);
```

SurrealQL:

```sql
SELECT *
FROM order
WHERE id = $orderId
FETCH customer, products
LIMIT 1;
```

---

## Filter Behavior — Link and Multi-Parameter Where

### Problem

For `Record` subclasses, standard LINQ dot-notation works in Where clauses:
```csharp
session.Query<Order>()
    .Where(o => o.Customer!.Name == "Alice")
    .ToListAsync();
```
→ `SELECT * FROM order WHERE customer.name = 'Alice';`

For `Entity<TId>` and POCO types, the FK field may be a plain typed value (long/string/etc), not a `RecordId`. `Where(x => CustomerId == 5)` is valid. To filter by properties of the referenced entity without a subquery, AeroDB constructs a SurrealDB record id expression with `type::record(target_table, scalar_fk)` and then uses dot traversal.

### Solution: Link + Multi-Parameter Lambda

Two-part API:

```csharp
// Part 1: Link — register the FK relationship on the query.
// Join<TTarget>() is a thin alias for Link<TTarget>() for RDBMS-oriented users.
.Link<TTarget>(o => o.FkField)

// Part 2: Where — target type is inferred from the preceding Link/Join chain.
// Second+ params reference linked entities.
.Where((o, c) => o.CreatedOn >= someDate && c.Name == "Alice")
```

Where `c` refers to the `Customer` entity linked via `o.Customer` (the record link property). The expression visitor distinguishes parameters:

| Root Parameter | Meaning | Translation |
|---|---|---|
| `Parameters[0]` (`o`) | Main entity | Standard property access |
| `Parameters[1]` (`c`) | Linked entity via `.Link<Customer>()` | FK-traversed access |

### SurrealQL Translation

**When FK field is a RecordId** (`record<T>` link — dot-notation):

```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)   // Customer is a record<T> property
    .Where((o, c) => o.CreatedOn >= someDate && c.Name == "Alice")
    .ToListAsync();
```

```surql
SELECT * FROM `order` WHERE created_on >= $p0 AND customer.name = $p1;
```

The dot-notation follows the configured AeroDB naming policy. With the default `SnakeCaseLower` policy, CLR `Customer.Name` maps to SurrealDB `customer.name`.

**When FK field is a typed scalar value** (long/string/etc — computed record id dot traversal):

```csharp
session.Query<EntityOrder>()
    .Link<EntityCustomer>(o => o.CustomerId)   // CustomerId is a typed FK (long)
    .Where((o, c) => c.Name == "Alice")
    .ToListAsync();
```

```surql
SELECT * FROM `entity_order`
WHERE type::record("entity_customer", customer_id).name = $p0;
```

FK type detection: The visitor inspects the property type of the FK field on the source type. If it's `RecordId`/`RecordIdOf<T>` or the property type implements `IRecord` → direct dot-notation. If it's `long`/`string`/`int`/`Guid` → `type::record(target_table, fk_field).field`.

Important: for `Entity<TId>` shim-backed types, the shim does not persist a separate scalar `Id` field. The generated shim derives from SurrealDB `Record`, skips the entity `Id` member, and uses the native lowercase `id` record id. The typed entity `Id` is restored during materialization.

Therefore typed-FK translation must not rely on a persisted scalar `Id` column on the target table. If the source FK stores a scalar value, AeroDB converts the FK value into the target record id shape with `type::record(...)` before traversing related fields.

### Legacy/convention shorthand: `Where<TTarget>()`

Auto-infers the FK field by strict convention when no explicit `.Link()` is needed:

```csharp
session.Query<Order>()
    .Where((Order o, Customer c) => c.Name == "Alice")
    .ToListAsync();
```
→ `SELECT * FROM `order` WHERE customer.name = $p0;`

This shorthand is only convenience sugar over the same `.Link<TTarget>(...)` infrastructure. It must not introduce a separate translation path. The preferred explicit relationship API is `.Link<TTarget>(...).Where((source, target) => ...)` or `.Join<TTarget>(...).Where((source, target) => ...)`, where the target type is inferred from the linked-query wrapper.

The convention resolves the two-parameter linked `Where` target as follows:

- Record-link property: field/property named after the target relationship, e.g. `Customer`.
- Typed FK property: field/property named `{short target name}Id`, e.g. `CustomerId`.
- Target identity: always resolves to the target table's native SurrealDB `id`, not a persisted scalar `Id` field.

For `EntityCustomer`, the short target name may normalize common AeroDB entity prefixes/suffixes so `EntityCustomer` can resolve `CustomerId` instead of requiring `EntityCustomerId`. If this cannot be resolved deterministically, throw and require explicit `.Link<TTarget>(...)`.

Resolution order:

1. **Explicit `.Link<TTarget>(...)`** — highest priority (manual override)
2. **`Schema.For<T>().ForeignKey<TTarget>()`** — declared in schema config
3. **Convention** — `{TTarget.Name}Id` on source type
4. **Attribute** — `[ForeignKey(typeof(TTarget))]` on the FK property

If convention or metadata finds zero candidates or more than one candidate, reject the shorthand with a clear error and tell the caller to use explicit `.Link<TTarget>(...)`.

### Composition: Multiple Links

```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)      // RecordId FK → dot-notation
    .Link<Product>(o => o.ProductId)      // typed FK → type::record(...).field
    .Where((o, c, p) => o.CreatedOn >= someDate && c.Name == "Alice" && p.Price > 50)
    .ToListAsync();
```

Equivalent RDBMS-friendly spelling:

```csharp
session.Query<Order>()
    .Join<Customer>(o => o.Customer)
    .Join<Product>(o => o.ProductId)
    .Where((o, c, p) => o.CreatedOn >= someDate && c.Name == "Alice" && p.Price > 50)
    .ToListAsync();
```

Generated SurrealQL (mixed: Customer is RecordId, Product is typed):

```surql
SELECT * FROM `order` WHERE
    created_on >= $p0
    AND customer.name = $p1
    AND type::record("product", product_id).price > $p2;
```

### Composition with Fetch

`.Link()` (WHERE filter) and `.Fetch()` (eager loading) are orthogonal and compose naturally:

```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)
    .Fetch(o => o.Customer)
    .Where((o, c) => c.Name == "Alice")
    .ToListAsync();
```

```surql
SELECT * FROM `order` WHERE customer.name = $p0 FETCH `customer`;
```

### Implementation

The visitor needs:

1. A `List<LinkRegistration>` on `SableQueryable<T>`, populated by `.Link<TTarget>()` / `.Join<TTarget>()`
2. A context dictionary mapping `ParameterExpression` → link registration for multi-param lambdas
3. FK type detection (RecordId vs typed) at translation time
4. Conditional coalescing: multiple conditions on the same linked scalar-FK entity share the same `type::record(...)` base expression

```csharp
internal sealed record LinkRegistration(
    Type TargetType,
    string TargetTable,
    string FkFieldName,
    bool FkIsRecordId
);
```

---

## Schemafull vs Schemaless

## SCHEMAFULL

For `SCHEMAFULL`, AeroDB should emit field definitions for relationship fields.

Single relationship:

```sql
DEFINE FIELD customer
ON TABLE order
TYPE record<customer>;
```

Many relationship:

```sql
DEFINE FIELD products
ON TABLE order
TYPE array<record<product>>;
```

This gives SurrealDB type-level validation.

## SCHEMALESS

For `SCHEMALESS`, the same values can be stored without defining fields first:

```sql
CREATE order:1 SET
    customer = customer:troy,
    products = [product:keyboard, product:mouse];
```

However, AeroDB should still keep mapping metadata internally so the LINQ provider can safely translate expressions such as:

```csharp
x.Customer.FirstName
x.Products.Select(p => p.Name)
```

Without mapping metadata, those expressions should not be treated as relationships automatically.

---

## Property-Based vs No-CLR-Property Relationships

AeroDB should support both property-based and no-property relationship mapping.

## Property-based mapping

Use this when the domain model has a navigation property.

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer);

Schema.For<Order>()
    .HasMany(x => x.Products);
```

This supports strongly typed LINQ:

```csharp
x.Customer.FirstName
x.Products.Select(p => p.Name)
```

because the expression tree contains real CLR members.

## No-CLR-property mapping

Use this when the relationship exists in storage but not in the CLR type.

```csharp
Schema.For<Order>()
    .HasOne<Customer>();

Schema.For<Order>()
    .HasMany<Product>();
```

By default, field names should be inferred from the related type using AeroDB's naming policy:

```text
Customer -> customer
Product -> product/products depending on convention
```

But explicit names must be supported:

```csharp
Schema.For<Order>()
    .HasOne<Customer>("customer");

Schema.For<Order>()
    .HasMany<Product>("products");
```

No-property mappings cannot support normal LINQ member access like:

```csharp
x.Customer.FirstName
```

because `Customer` is not a CLR property on `Order`.

For this case, AeroDB may later expose provider-neutral dynamic APIs such as:

```csharp
session.Query<Order>()
    .SelectRelated<Customer>("customer", c => c.FirstName);
```

or provider-specific raw SurrealQL escape hatches.

---

## Naming Policy

Do not hardcode `.ToLowerInvariant()` everywhere.

Use a configurable naming policy exposed through AeroDB options:

```csharp
options.Schema.Case = AeroDbNameCase.SnakeCaseLower;
```

Define the built-in case enum:

```csharp
public enum AeroDbNameCase
{
    SnakeCaseLower,
    CamelCase,
    PascalCase
}
```

Define a naming policy abstraction so generated metadata, schema generation, and query translation use one source of truth:

```csharp
public interface IAeroDbNamingPolicy
{
    string TableName(Type type);
    string FieldName(MemberInfo member);
    string FieldName(string clrName);
}
```

Built-in cases:

- `SnakeCaseLower`
- `CamelCase`
- `PascalCase`

Recommended explicit configuration for new SurrealDB relationship work:

```text
PascalCase CLR type/property -> snake_case_lower storage field/table
```

For SurrealDB specifically, choose one consistent configured policy and apply it everywhere:

- table names
- field names
- relationship storage fields
- schema generation
- LINQ member translation
- `Fetch(...)`
- `Include(...)`
- `Link(...)`
- convention-based FK resolution

Examples if using snake_case_lower:

```text
Customer -> customer
CustomerProfile -> customer_profile
LineItem -> line_item
Order.Products -> products
```

Examples if using camelCase:

```text
Customer -> customer
CustomerProfile -> customerProfile
LineItem -> lineItem
Order.Products -> products
```

AeroDB should allow per-table and per-field overrides.

Proposed override API shape:

```csharp
options.Schema.For<Order>()
    .TableName("sales_order")
    .FieldName(x => x.CreatedOn, "created_at");

options.Schema.For<Order>()
    .HasOne(x => x.Customer)
    .FieldName("customer");
```

This is a foundation task. Relationship implementation should use the naming policy instead of introducing new local calls to `.ToLowerInvariant()`, `Snake(...)`, or member-name passthroughs.

---

## Relationship Metadata Model

Suggested internal metadata shape:

```csharp
public enum RelationshipKind
{
    HasOne,
    HasMany
}

public enum RelationshipStorageModel
{
    RecordLink,
    RecordLinkArray,
    GraphEdge
}

public sealed class RelationshipMapping
{
    public Type SourceType { get; init; } = default!;
    public string SourceTableName { get; init; } = "";

    public Type TargetType { get; init; } = default!;
    public string TargetTableName { get; init; } = "";

    public string? ClrMemberName { get; init; }
    public string StorageFieldName { get; init; } = "";

    public RelationshipKind Kind { get; init; }
    public RelationshipStorageModel StorageModel { get; init; }

    public bool Required { get; init; }
    public bool Nullable { get; init; }
}
```

For graph support, keep graph mappings separate or set `StorageModel = GraphEdge` only for the existing graph API.

---

## Recommended Public API

## Preferred schema API

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer);

Schema.For<Order>()
    .HasMany(x => x.Products);
```

## Optional modifiers

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer)
        .Required();

Schema.For<Order>()
    .HasOne(x => x.SalesRep)
        .Optional();

Schema.For<Order>()
    .HasMany(x => x.Products)
        .FieldName("products");
```

## No-property overloads

```csharp
Schema.For<Order>()
    .HasOne<Customer>();

Schema.For<Order>()
    .HasOne<Customer>("customer");

Schema.For<Order>()
    .HasMany<Product>();

Schema.For<Order>()
    .HasMany<Product>("products");
```

## Aliases

Aliases may be provided for DX, but documentation should choose one preferred style.

Potential aliases:

```csharp
Relationship(...)
Reference(...)
Link(...)
Relate(...)
```

Recommendation:

- Document `HasOne` / `HasMany` for ordinary record relationships.
- Reserve `Relate` for graph relationships if AeroDB already uses graph terminology.
- Use `Include` only for query-time eager loading.

---

## SurrealDB Provider Translation Rules

## Schema generation

### HasOne

```csharp
Schema.For<Order>().HasOne(x => x.Customer);
```

Generates:

```sql
DEFINE FIELD customer
ON TABLE order
TYPE record<customer>;
```

If optional:

```sql
DEFINE FIELD customer
ON TABLE order
TYPE option<record<customer>>;
```

or a union with `NONE`, depending on the SurrealDB version and AeroDB's type-generation strategy.

### HasMany

```csharp
Schema.For<Order>().HasMany(x => x.Products);
```

Generates:

```sql
DEFINE FIELD products
ON TABLE order
TYPE array<record<product>>;
```

If optional:

```sql
DEFINE FIELD products
ON TABLE order
TYPE option<array<record<product>>>;
```

or a union with `NONE`, depending on the SurrealDB version and AeroDB's type-generation strategy.

---

## Query generation

## Member access projection

```csharp
x.Customer.FirstName
```

SurrealQL:

```sql
customer.first_name
```

## Collection member projection

```csharp
x.Products.Select(p => p.Name)
```

SurrealQL:

```sql
products.name
```

## Include single

```csharp
.Include(x => x.Customer)
```

SurrealQL:

```sql
FETCH customer
```

## Include many

```csharp
.Include(x => x.Products)
```

SurrealQL:

```sql
FETCH products
```

## Multiple includes

```csharp
.Include(x => x.Customer)
.Include(x => x.Products)
```

SurrealQL:

```sql
FETCH customer, products
```

---

## Mutation Semantics

## Assign single relationship

C# conceptual behavior:

```csharp
order.Customer = customer;
await session.StoreAsync(order);
```

SurrealQL:

```sql
UPDATE order:1 SET customer = customer:troy;
```

## Assign many relationship

C# conceptual behavior:

```csharp
order.Products = [keyboard, mouse];
await session.StoreAsync(order);
```

SurrealQL:

```sql
UPDATE order:1 SET products = [product:keyboard, product:mouse];
```

## Add one related item

Potential AeroDB API:

```csharp
await session.Relationships<Order>()
    .For(orderId)
    .Add(x => x.Products, productId);
```

SurrealQL:

```sql
UPDATE order:1 SET products += product:monitor;
```

If uniqueness is desired:

```sql
UPDATE order:1 SET products = array::distinct(array::append(products, product:monitor));
```

## Remove one related item

Potential AeroDB API:

```csharp
await session.Relationships<Order>()
    .For(orderId)
    .Remove(x => x.Products, productId);
```

SurrealQL:

```sql
UPDATE order:1 SET products -= product:mouse;
```

---

## Error Handling

The LINQ provider should reject ambiguous or unsupported relationship traversals early.

Examples:

```csharp
x.Customer.FirstName
```

If `Order.Customer` is not configured with `HasOne`, throw:

```text
Cannot translate member access 'Order.Customer.FirstName'. The member 'Order.Customer' is not configured as an AeroDB relationship. Configure it with Schema.For<Order>().HasOne(x => x.Customer).
```

For collection traversal:

```csharp
x.Products.Select(p => p.Name)
```

If `Order.Products` is not configured with `HasMany`, throw:

```text
Cannot translate collection relationship access 'Order.Products'. The member 'Order.Products' is not configured as an AeroDB relationship. Configure it with Schema.For<Order>().HasMany(x => x.Products).
```

---

## Provider Differences

## SurrealDB

- `HasOne` maps to `record<table>`.
- `HasMany` maps to `array<record<table>>`.
- `Include` may map to `FETCH` for direct record links; reverse and typed-FK includes may use LET/subquery behavior.
- Related field projection maps to dot traversal such as `customer.first_name` or `products.name` under the default `SnakeCaseLower` naming policy.

## MartenDB

- `HasOne` likely maps to an ID/reference field or serialized reference strategy.
- `HasMany` likely maps to a collection of IDs or embedded references depending on provider capability.
- `Include` maps to Marten-style include/load behavior.
- Deep related projections may require additional query/load steps or may be unsupported unless denormalized.

## Polecat

- Implement based on provider storage model.
- Preserve the same AeroDB relationship metadata.

---

## Implementation Tasks

1. Add the naming policy foundation:
   - `AeroDbNameCase`
   - `IAeroDbNamingPolicy`
   - built-in snake_case, camelCase, and PascalCase policies
   - per-table and per-field overrides
   - one shared policy path for generated metadata, schema generation, query translation, `Fetch`, `Include`, and `Link`
2. Add relationship metadata types.
3. Add schema builder methods:
   - `HasOne(Expression<Func<T, TRelated>> member)`
   - `HasOne<TRelated>()`
   - `HasOne<TRelated>(string fieldName)`
   - `HasMany(Expression<Func<T, IEnumerable<TRelated>>> member)`
   - `HasMany<TRelated>()`
   - `HasMany<TRelated>(string fieldName)`
4. Add optional modifiers:
   - `.Required()`
   - `.Optional()`
   - `.FieldName(string name)`
5. Update SurrealDB schema generator:
   - `HasOne` -> `TYPE record<table>`
   - `HasMany` -> `TYPE array<record<table>>`
6. Update LINQ provider expression analysis:
   - Detect member access through configured `HasOne` relationships.
   - Detect collection access through configured `HasMany` relationships.
   - Reject unconfigured relationship traversal.
7. Update SurrealDB query translator:
   - Single relationship projection -> `field.subField`
   - Many relationship projection -> `field.subField`
   - Include -> `FETCH field`
8. Add mutation helpers for assigning, adding, and removing record links.
9. Add tests for schema generation, query translation, includes, and mutation behavior.
10. Keep graph edge relationship support separate from record relationship support.
11. Add optional relationship DDL modifiers:
    - `Reference()`
    - `OnDeleteIgnore()`
    - `OnDeleteUnset()`
    - `OnDeleteCascade()`
    - `OnDeleteThen(string surqlBlock)` or a safer builder if feasible
12. Add uniqueness support for record-link arrays:
    - schema/index-level uniqueness where applicable
    - mutation-level idempotent add using `array::add`
13. Resolve the existing `DocumentMapping<T>.ForeignKey<TChild>()` conflict before adding relationship-driven FK APIs:
    - keep it metadata-only and document the distinction, or
    - deprecate and delegate it to the new relationship model in a later breaking-change phase.

### Link + Multi-Parameter Filter Tasks

14. Add `LinkRegistration` record type and `List<LinkRegistration>` to `SableQueryable<T>`.
15. Add `.Link<TTarget>(Expression<Func<T, object>> fkSelector)` and `.Join<TTarget>(...)` methods on `ISableQueryable<T>`. `Join` delegates to `Link` and has no separate semantics.
16. Extend `SurrealExpressionVisitor` to accept link registrations and handle multi-parameter lambdas:
    - Detect which `ParameterExpression` a `MemberExpression` roots in.
    - For main-entity params (index 0): standard translation (existing path).
    - For linked-entity params (index 1+): translate via link registration.
17. Implement FK type detection at translation time — `RecordId`/`RecordIdOf<T>` → direct dot-notation; `long`/`string`/`int`/`Guid` → `type::record(target_table, fk_field).field`.
18. Ensure linked predicates share a single parameter allocator so generated `$p0`, `$p1`, etc. cannot collide.
19. Make `.Link<TTarget>(...)` / `.Join<TTarget>(...)` return a typed linked-query wrapper so the immediately chained `.Where((TSource source, TTarget target) => ...)` infers target types without redundant generic arguments. Keep convention-based `Where<TTarget>()` separate and fail fast on ambiguity.
20. Implement multi-link composition: 3+ parameter Where with multiple `.Link()` registrations.
21. Implement conditional coalescing: multiple conditions on the same linked scalar-FK entity reuse the same `type::record(...)` base expression.
23. Add test cases:
    - Naming policy output for schema generation and query translation under snake_case, camelCase, and PascalCase
    - Link + Where with RecordId FK (dot-notation path)
    - Link + Where with typed FK (`type::record(...)` path)
    - typed `Link/Join -> Where(...)` inference
    - `Where<TTarget>()` shorthand (convention-based FK resolution)
    - Multiple links composition (3+ params)
    - Mixed WHERE conditions (main entity + linked entity in one expression)
    - Linked predicate parameter names do not collide
    - Cross-paradigm: Record subclass, Entity<TId>, POCO
    - Error: unconfigured link with no convention match
    - Error: convention shorthand finds multiple FK candidates
    - Composition with Fetch, Include, IncludeReverse

---

## Test Cases

## Schema generation: HasOne

Input:

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer);
```

Expected SurrealQL:

```sql
DEFINE FIELD customer
ON TABLE order
TYPE record<customer>;
```

## Schema generation: HasMany

Input:

```csharp
Schema.For<Order>()
    .HasMany(x => x.Products);
```

Expected SurrealQL:

```sql
DEFINE FIELD products
ON TABLE order
TYPE array<record<product>>;
```

## Include translation

Input:

```csharp
session.Query<Order>()
    .Include(x => x.Customer)
    .Include(x => x.Products)
    .Where(x => x.Id == orderId);
```

Expected SurrealQL shape:

```sql
SELECT *
FROM order
WHERE id = $orderId
FETCH customer, products;
```

## Projection translation

Input:

```csharp
session.Query<Order>()
    .Where(x => x.Id == orderId)
    .Select(x => new
    {
        x.Id,
        x.Customer.FirstName
    });
```

Expected SurrealQL shape:

```sql
SELECT id, customer.first_name AS customer_first_name
FROM order
WHERE id = $orderId;
```

## Missing mapping error

Input:

```csharp
session.Query<Order>()
    .Select(x => x.Customer.FirstName);
```

without:

```csharp
Schema.For<Order>().HasOne(x => x.Customer);
```

Expected:

```text
Clear translation error explaining that Order.Customer is not configured as a relationship.
```

## Link + Where (RecordId FK — dot-notation)

Input (Record subclass):
```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)   // record<T> property
    .Where((o, c) => o.CreatedOn >= someDate && c.Name == "Alice")
    .ToListAsync();
```

Expected SurrealQL:
```sql
SELECT * FROM `order` WHERE created_on >= $p0 AND customer.name = $p1;
```

## Link + Where (typed FK — computed record id dot traversal)

Input (Entity<TId>):
```csharp
session.Query<EntityOrder>()
    .Link<EntityCustomer>(o => o.CustomerId)  // typed FK (long)
    .Where((o, c) => c.Name == "Alice")
    .ToListAsync();
```

Expected SurrealQL:
```sql
SELECT * FROM `entity_order` WHERE type::record("entity_customer", customer_id).name = $p0;
```

If `CustomerId` stores a scalar typed id rather than a full SurrealDB record id, the translator must use AeroDB's record-id conversion/extraction strategy rather than assuming a persisted scalar `Id` column exists on the target table.

## Where<TTarget> shorthand (convention-based FK)

Input (Record subclass):
```csharp
session.Query<Order>()
    .Where((Order o, Customer c) => c.Name == "Bob")
    .ToListAsync();
```

Expected: Same as explicit `.Link<Customer>(o => o.Customer).Where((o, c) => ...)` — convention resolves `Customer` → FK field `Customer`.

Input (Entity<TId>):
```csharp
session.Query<EntityOrder>()
    .Where((EntityOrder o, EntityCustomer c) => c.Name == "Alice")
    .ToListAsync();
```

Expected: Same as explicit `.Link<EntityCustomer>(o => o.CustomerId).Where((o, c) => ...)` — convention resolves `EntityCustomer` → FK field `CustomerId`.

## Multiple links composition (mixed types)

Input:
```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)      // RecordId FK → dot-notation
    .Link<Product>(o => o.ProductId)      // typed FK → type::record(...).field
    .Where((o, c, p) => o.CreatedOn >= someDate && c.Name == "Alice" && p.Price > 50)
    .ToListAsync();
```

Expected SurrealQL:
```sql
SELECT * FROM `order` WHERE created_on >= $p0 AND customer.name = $p1 AND type::record("product", product_id).price > $p2;
```

## Link + Where composed with Fetch

Input:
```csharp
session.Query<Order>()
    .Link<Customer>(o => o.Customer)
    .Fetch(o => o.Customer)
    .Where((o, c) => c.Name == "Alice")
    .ToListAsync();
```

Expected SurrealQL:
```sql
SELECT * FROM `order` WHERE customer.name = $p0 FETCH `customer`;
```

---

## Final Recommendation

For AeroDB, implement ordinary record relationships as a first-class provider-neutral concept:

```csharp
Schema.For<Order>()
    .HasOne(x => x.Customer)
    .HasMany(x => x.Products);
```

For SurrealDB, map those to:

```sql
DEFINE FIELD customer ON TABLE order TYPE record<customer>;
DEFINE FIELD products ON TABLE order TYPE array<record<product>>;
```

Keep graph relationships separate and continue using AeroDB's existing graph API for SurrealDB `RELATE` edge records.

---

## Current Implementation Findings

- `Entity<TId>` shims do not persist a separate scalar `Id` field. The shim derives from SurrealDB `Record`, skips the entity `Id` member, and uses the native lowercase `id` record id.
- Any typed-FK implementation must avoid `SELECT VALUE Id FROM target`.
- `Where<TTarget>()` remains viable in v1 only as strict convention sugar over `.Link<TTarget>()`; ambiguity must fail fast.
- Preferred explicit linked queries should use `.Link<TTarget>(...).Where((source, target) => ...)` or `.Join<TTarget>(...).Where((source, target) => ...)`; the linked wrapper carries target type information so the `Where` call does not need redundant generic arguments.
- `REFERENCE ON DELETE` is viable for record-link DDL and should be added as an optional relationship modifier.
- Idempotent add semantics are viable through SurrealDB array helpers such as `array::add`, with `+=` / `-=` still usable for direct append/remove mutations.

## Corrected Phase Plan

1. Naming policy, relationship metadata, schema generation, `Include`, and `Select` projection through record links.
2. Validated direct dot-traversal `Where` through configured record links.
3. SurrealDB-specific `.Link<TTarget>()` / `.Join<TTarget>()` plus typed-wrapper multi-parameter `Where`.
4. Strict `.Where<TTarget>()` shorthand over `.Link<TTarget>()`, enabled only when exactly one FK can be resolved.
5. Typed scalar FK support, `REFERENCE ON DELETE`, uniqueness modifiers, and mutation helpers.
