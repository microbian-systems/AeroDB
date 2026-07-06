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
    firstName = "Troy",
    lastName = "Robinson";

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
    customer.firstName AS customerFirstName,
    customer.lastName AS customerLastName
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

Use a configurable naming policy.

Recommended default:

```text
PascalCase CLR type/property -> camelCase or snake_case_lower storage field/table
```

For SurrealDB specifically, choose one consistent default and apply it everywhere.

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
customer.firstName
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
- `Include` maps to `FETCH`.
- Related field projection maps to dot traversal such as `customer.firstName` or `products.name`.

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

1. Add relationship metadata types.
2. Add schema builder methods:
   - `HasOne(Expression<Func<T, TRelated>> member)`
   - `HasOne<TRelated>()`
   - `HasOne<TRelated>(string fieldName)`
   - `HasMany(Expression<Func<T, IEnumerable<TRelated>>> member)`
   - `HasMany<TRelated>()`
   - `HasMany<TRelated>(string fieldName)`
3. Add optional modifiers:
   - `.Required()`
   - `.Optional()`
   - `.FieldName(string name)`
4. Add naming policy support for inferred field and table names.
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
SELECT id, customer.firstName AS customerFirstName
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
