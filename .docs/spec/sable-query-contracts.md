# Sable Composable Query Contracts

> **Status:** Proposed public contract for RF-003; examples intentionally do not
> compile until their implementation slice is enabled.
> **Last updated:** 2026-07-22
> **Applies to:** The `AeroDB.Sable` query compiler refactor described in
> [`../sable-refactor.md`](../sable-refactor.md).

## Purpose

This document freezes the intended user experience before the new query compiler
is implemented. It is a public-contract specification, not a claim that every
example works in the current alpha.

The examples answer one question: can an application express SurrealDB's
multi-model scenarios through Sable's normal LINQ, fluent session, and schema
APIs without dropping to raw SurrealQL?

The target answer is yes for the scenarios below. `RawQueryAsync` remains the
explicit escape hatch for language features Sable has not modeled.

## Contract rules

1. `IQuerySession.Query<T>()`, normal LINQ operators, projections, paging,
   `Store`, delete, `LoadAsync`, and `SaveChangesAsync` retain their familiar
   public shape.
2. `ISurrealDbQueryable<T>` is renamed to `ISableQueryable<T>` during alpha.
   No compatibility alias is retained.
3. SurrealDB-specific read operators extend `IQueryable<T>` and contribute
   typed nodes to the same query plan. They do not execute, own a second query
   renderer, or hide an intermediate database round trip.
4. Search configuration may use a scoped fluent clause builder, but completing
   that builder returns the original composable query. It is not a terminal
   `ISearchQuery<T>` pipeline.
5. Graph traversal changes the query's element type and remains composable with
   filters, projections, search, ordering, paging, and live subscriptions.
6. Geometry and ranking operations used inside LINQ are compiler-recognized
   marker methods. Calling a marker method in normal CLR execution throws a
   clear `NotSupportedException`.
7. Runtime values become command parameters. Identifiers and schema metadata
   are validated separately and never masquerade as value parameters.
8. Every raw fragment API includes `Raw` in its name and documents that it
   bypasses typed validation.
9. The AST, compiler passes, renderer, and compiled-plan implementation remain
   internal.

## Foundational inspection contract

The queryable returned directly by `Query<T>()` is Sable-specific, while normal
LINQ operators retain their standard `IQueryable<T>` signatures. Inspection is
therefore available both on the Sable interface and as an `IQueryable<T>`
extension that verifies the provider:

```csharp
public interface ISableQueryable<T> : IOrderedQueryable<T>
{
    SableCommand ToCommand();
}

public sealed record SableCommand(
    string CommandText,
    IReadOnlyDictionary<string, object?> Parameters)
{
    public override string ToString() => CommandText;
}

public static SableCommand ToCommand<T>(this IQueryable<T> query);
```

The concrete queryable overrides `object.ToString()` and returns only the
parameterized SurrealQL text. It does not execute the query or render parameter
values.

The two compiled-query forms have slightly different inspection contracts:

- `store.CompileQuery<T>(...)` returns a Sable-owned compiled object whose
  `ToString()` is the parameterized template and whose `ToCommand()` exposes the
  template plus its current parameter bindings.
- A consumer-owned `ICompiledQuery<TDoc, TOut>` cannot be forced to override
  `object.ToString()`. Its exact schema-, policy-, and session-aware command is
  exposed through `session.ToCommand(compiledQuery)`. The cached internal plan
  also overrides `ToString()` for debugger display.

This avoids requiring consumer compiled-query classes to inherit a Sable base
class solely for diagnostics.

## Proposed composable primitives

The names in this section are the proposed alpha contract to be reviewed before
RF-010 begins.

### Search and ranking

```csharp
IQueryable<T> Search<T>(
    this IQueryable<T> source,
    Action<ISableSearchClause<T>> configure);

public interface ISableSearchClause<T>
{
    ISableSearchClause<T> MatchText<TValue>(
        Expression<Func<T, TValue>> field,
        string query,
        double weight = 1);

    ISableSearchClause<T> NearestNeighbors(
        Expression<Func<T, float[]>> field,
        float[] vector,
        int neighbors,
        int candidates = 100);

    ISableSearchClause<T> FuseByReciprocalRank(int k = 60, int limit = 80);
}

public static class SableSearch
{
    public static double Score();
    public static double TextScore();
    public static double VectorDistance();
}
```

`Search(...)` scopes matching and fusion so the compiler can assign SurrealQL
match references internally. Users never pass numeric `search::score(0)` index
values. Ranking markers refer to the current search scope and fail validation if
used without one.

### Graph traversal

```csharp
ISableTraversalQueryable<TSource, TSource> Traverse<TSource>(
    this IQueryable<TSource> source)
    where TSource : class;

public interface ISableTraversalQueryable<TRoot, TNode>
    : ISableQueryable<TNode>
    where TRoot : class
    where TNode : class
{
    ISableTraversalQueryable<TRoot, TTarget> Out<TEdge, TTarget>(
        GraphTraversalOptions? options = null)
        where TEdge : EdgeRecord
        where TTarget : class;

    ISableTraversalQueryable<TRoot, TTarget> In<TEdge, TTarget>(
        GraphTraversalOptions? options = null)
        where TEdge : EdgeRecord
        where TTarget : class;

    ISableTraversalQueryable<TRoot, TTarget> Both<TEdge, TTarget>(
        GraphTraversalOptions? options = null)
        where TEdge : EdgeRecord
        where TTarget : class;

    ISableTraversalQueryable<TRoot, TNode> Where(
        Expression<Func<TNode, bool>> predicate);

    ISableQueryable<GraphPath<TRoot, TNode>> AsPaths();
}

public sealed record GraphTraversalOptions
{
    public static GraphTraversalOptions Depth(int depth);
    public static GraphTraversalOptions Depth(int minimum, int maximum);
}
```

`Traverse()` preserves the typed source while entering graph-specific type
state. Each traversal call contributes one typed traversal source node, and
repeated calls represent multi-hop paths. Path materialization is an explicit
result-shaping operator; ordinary traversal returns target documents.

The edge generic is first because SurrealQL reads `source -> edge -> target`.
The current unused `IGraphQuery<T>.Out<TTarget, TEdge>()` order is not preserved.

### Spatial expressions

```csharp
public static class SableGeo
{
    public static bool Within(Geometry value, Geometry boundary);
    public static bool Intersects(Geometry left, Geometry right);
    public static double Distance(Geometry left, Geometry right);
}
```

These methods are used inside ordinary `Where`, `OrderBy`, and `Select`
expressions. The terminal `ISpatialQuery<T>` renderer is removed.

### Live subscriptions

```csharp
Task<IAeroDBLiveQuery<T>> WatchAsync<T>(
    this IQueryable<T> query,
    LiveQueryOptions? options = null,
    CancellationToken cancellationToken = default);
```

`WatchAsync` wraps the already validated select plan in `LIVE SELECT`. It does
not rebuild the filter or projection through a live-query-specific translator.
Existing bounded channels, cancellation, and change wrappers remain usable.

### Correlated subqueries

```csharp
public static class SableQuery
{
    public static bool Exists<T>(Expression<Func<T, bool>> predicate)
        where T : class;
}
```

This explicit marker is preferred over capturing an `IQuerySession` inside an
expression tree. It makes the correlated subquery visible to validation and
keeps execution out of query construction.

### Typed access and event definitions

Authentication and database-event definitions use schema builders backed by
the same statement and expression nodes as runtime commands:

```csharp
options.Schema.Access<User>("account", access => ...);

options.Events.Triggers.AddTrigger<Order>("reserve_inventory", trigger => ...);
```

The final builder members are specified in Scenarios 3 and 13. The current
string properties (`SignupQuery`, `SigninQuery`, `WhenCondition`, and `Action`)
are not the typed contract. They remain only as explicitly named raw escape
hatches if required during migration.

## Capability examples

The model names are illustrative. Each `Expected command` block defines the
semantic command shape; command-generation tests will pin exact quoting,
parameter numbering, and whitespace for the supported SurrealDB 3.x baseline.

### SQC-01: Multi-model query

Record links, a correlated document subquery, a graph edge, projection, and
paging participate in one plan:

```csharp
var query = session.Query<Order>()
    .Link<Customer>(order => order.CustomerId)
    .Where<Customer>((order, customer) => customer.Region == region)
    .Where(order => SableQuery.Exists<Inventory>(stock =>
        stock.ProductId == order.ProductId && stock.OnHand > 0))
    .Traverse()
    .Out<Contains, Product>()
    .Where(product => product.Active)
    .Select(product => new ProductSummary(product.Id, product.Name, product.Price))
    .Take(25);

SableCommand command = query.ToCommand();
```

Expected command:

```surql
SELECT id, name, price
FROM (
    SELECT ->contains->product
    FROM order
    WHERE <linked customer predicate>
      AND EXISTS (SELECT VALUE id FROM inventory WHERE <correlated predicate>)
)
WHERE active = $p3
LIMIT 25;
```

Status: **Proposed**. Ordinary LINQ and record links exist; correlated subquery
and graph-source composition require RF-030, RF-060, and RF-061.

### SQC-02: ACID transactions

The existing document-session unit of work is the public transaction composer:

```csharp
await using var transaction = await session.BeginTransactionAsync(ct);

session.Store(order);
session.Relate<Purchased>(customerRecordId, productRecordId, new Purchased
{
    PurchasedAt = clock.UtcNow
});
session.Patch<Product>(product.Id).Increment(x => x.Stock, -1);

await session.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

Expected command/operation plan:

```surql
BEGIN TRANSACTION;
CREATE order CONTENT $p0;
RELATE $p1->purchased->$p2 CONTENT $p3;
UPDATE product:$p4 SET stock += $p5;
COMMIT TRANSACTION;
```

Status: **Public syntax exists**. RF-050 through RF-052 replace internal string
assembly with typed statements while preserving this API. No parallel public
transaction-script DSL is planned.

### SQC-03: Built-in authentication

```csharp
options.Schema.Access<User>("account", access => access
    .Record()
    .SignUp(signup => signup
        .Create<User>()
        .Set(user => user.Email, signup.Parameter<string>("email"))
        .Set(user => user.PasswordHash,
            SableCrypto.Argon2.Generate(signup.Parameter<string>("password"))))
    .SignIn(signin => signin
        .Query<User>()
        .Where(user =>
            user.Email == signin.Parameter<string>("email") &&
            SableCrypto.Argon2.Verify(
                user.PasswordHash,
                signin.Parameter<string>("password")))
        .SingleOrDefault())
    .Authenticate(user => user.Enabled)
    .TokenDuration(TimeSpan.FromHours(1))
    .SessionDuration(TimeSpan.FromDays(7)));
```

Expected command:

```surql
DEFINE ACCESS account ON DATABASE TYPE RECORD
    SIGNUP (CREATE user SET email = $email, password_hash = crypto::argon2::generate($password))
    SIGNIN (SELECT * FROM user WHERE email = $email AND crypto::argon2::compare(password_hash, $password))
    AUTHENTICATE $auth.enabled = true
    DURATION FOR TOKEN 1h, FOR SESSION 7d;
```

Status: **Proposed** for RF-070. The current `AccessDefinition` stores sign-up
and sign-in as raw strings and does not meet this contract. `DEFINE SCOPE` is not
used by the new typed API because Sable targets SurrealDB 3.x `DEFINE ACCESS`.

### SQC-04: Hybrid RAG

```csharp
var context = session.Query<KnowledgeChunk>()
    .Where(chunk => chunk.TenantId == tenantId && chunk.Published)
    .Search(search => search
        .MatchText(chunk => chunk.Title, prompt, weight: 2)
        .MatchText(chunk => chunk.Body, prompt)
        .NearestNeighbors(chunk => chunk.Embedding, embedding,
            neighbors: 40, candidates: 120)
        .FuseByReciprocalRank(k: 60, limit: 80))
    .OrderByDescending(chunk => SableSearch.Score())
    .Select(chunk => new RagContext(
        chunk.Id,
        chunk.Body,
        SableSearch.Score(),
        SableSearch.VectorDistance()))
    .Take(10);
```

Expected command: one parameterized multi-statement/derived-table plan with a
BM25-ranked source, a KNN-ranked source, reciprocal-rank fusion, the ordinary
filter applied consistently, a projected score/distance, and an outer limit.

Status: **Proposed** for RF-060. Current text/vector methods execute through a
separate terminal builder and cannot satisfy this composition contract.

### SQC-05: Graph RAG

```csharp
var context = session.Query<KnowledgeChunk>()
    .Where(chunk => chunk.TenantId == tenantId)
    .Search(search => search
        .MatchText(chunk => chunk.Body, prompt)
        .NearestNeighbors(chunk => chunk.Embedding, embedding, 20)
        .FuseByReciprocalRank())
    .Take(5)
    .Traverse()
    .Out<Mentions, Concept>(GraphTraversalOptions.Depth(1, 2))
    .Where(concept => concept.Confidence >= minimumConfidence)
    .Select(concept => new GraphContext(concept.Name, concept.Summary));
```

Expected command: the bounded search result is the source of a typed outgoing
`mentions` traversal; graph filtering and projection remain server-side in the
same command plan.

Status: **Proposed** for RF-060 and RF-061.

### SQC-06: Context expansion

```csharp
var context = session.Query<Concept>()
    .Where(concept => concept.Id == conceptId)
    .Traverse()
    .Out<RelatedTo, Concept>(GraphTraversalOptions.Depth(1, 3))
    .Where(concept => concept.Active)
    .OrderByDescending(concept => concept.Importance)
    .Select(concept => new ContextItem(concept.Id, concept.Name, concept.Summary))
    .Take(50);
```

Expected command: one bounded recursive outgoing traversal with target filter,
ordering, projection, and limit. The compiler rejects unbounded recursive
traversal unless the caller opts into it explicitly.

Status: **Proposed** for RF-061.

### SQC-07: Knowledge graphs

Typed edge writes continue through the document session, and typed traversal
uses the composed query provider:

```csharp
session.Relate<WorksFor>(personRecordId, companyRecordId, new WorksFor
{
    Role = "Engineer",
    Since = new DateOnly(2024, 1, 1)
});
await session.SaveChangesAsync(ct);

var graph = session.Query<Person>()
    .Where(person => person.Id == personId)
    .Traverse()
    .Out<WorksFor, Company>()
    .Out<LocatedIn, City>()
    .Where(city => city.CountryCode == countryCode)
    .Select(city => new KnowledgeNode(city.Id, city.Name));
```

Expected command: the write renders a typed `RELATE` statement inside
`SaveChangesAsync`; the read renders the `person -> works_for -> company ->
located_in -> city` path with parameterized predicates.

Status: **Partly available**. Typed edge writes and disconnected traversal
exist. Shared query-plan traversal and projection require RF-052 and RF-061.

### SQC-08: Agent memory

```csharp
var memories = session.Query<AgentMemory>()
    .Where(memory =>
        memory.AgentId == agentId &&
        memory.ExpiresAt > clock.UtcNow &&
        memory.Kind == requestedKind)
    .Search(search => search
        .NearestNeighbors(memory => memory.Embedding, embedding,
            neighbors: 30, candidates: 100))
    .OrderBy(memory => SableSearch.VectorDistance())
    .ThenByDescending(memory => memory.LastReinforcedAt)
    .Take(12);
```

Expected command: metadata and temporal predicates are combined with a KNN
predicate; vector distance and reinforcement time order the bounded result.

Status: **Proposed** for RF-060. The ordinary predicates already compile.

### SQC-09: Conversational memory

```csharp
var turns = session.Query<ConversationTurn>()
    .Where(turn =>
        turn.ConversationId == conversationId &&
        turn.ParticipantId == participantId &&
        turn.CreatedAt >= windowStart)
    .Search(search => search
        .NearestNeighbors(turn => turn.Embedding, embedding, 50))
    .OrderBy(turn => SableSearch.VectorDistance())
    .ThenByDescending(turn => turn.CreatedAt)
    .Skip(page * pageSize)
    .Take(pageSize)
    .Select(turn => new MemoryTurn(turn.Role, turn.Content, turn.CreatedAt));
```

Expected command: conversation, participant, and time-window predicates compose
with vector ranking, deterministic temporal tie-breaking, projection, and paging.

Status: **Proposed** for RF-060.

### SQC-10: Live queries

```csharp
var query = session.Query<ConversationTurn>()
    .Where(turn => turn.ConversationId == conversationId)
    .OrderBy(turn => turn.CreatedAt)
    .Select(turn => new MemoryTurn(turn.Role, turn.Content, turn.CreatedAt));

await using var subscription = await query.WatchAsync(
    new LiveQueryOptions { ChannelCapacity = 512 },
    ct);

await foreach (var change in subscription.Changes(ct))
{
    await HandleAsync(change, ct);
}
```

Expected command:

```surql
LIVE SELECT role, content, created_at
FROM conversation_turn
WHERE conversation_id = $p0
ORDER BY created_at;
```

Status: **Proposed** for RF-063. Current live query callbacks, backpressure,
cancellation, and change delivery exist, but filtering/projection use a separate
builder or a raw `whereClause` string.

### SQC-11: Recommendations

```csharp
var recommendations = session.Query<Product>()
    .Where(product => product.Active && product.InStock)
    .Search(search => search
        .NearestNeighbors(product => product.Embedding, profileEmbedding, 100))
    .Where(product => product.CategoryId == categoryId)
    .OrderBy(product => SableSearch.VectorDistance())
    .Select(product => new Recommendation(product.Id, product.Name, product.Price))
    .Take(20);
```

Expected command: vector candidate generation composes with business filtering,
distance ranking, projection, and limit. Graph-based recommendations use the
same query pipeline by replacing the search clause with a typed traversal.

Status: **Proposed** for RF-060 and RF-061.

### SQC-12: Geospatial queries

```csharp
var nearby = session.Query<StoreLocation>()
    .Where(store =>
        store.Open &&
        SableGeo.Within(store.Location, serviceArea))
    .OrderBy(store => SableGeo.Distance(store.Location, origin))
    .Select(store => new NearbyStore(
        store.Id,
        store.Name,
        SableGeo.Distance(store.Location, origin)))
    .Skip(offset)
    .Take(pageSize);
```

Expected command:

```surql
SELECT id, name, geo::distance(location, $p2) AS distance
FROM store_location
WHERE open = $p0 AND location INSIDE $p1
ORDER BY geo::distance(location, $p2)
START $p3 LIMIT $p4;
```

Status: **Proposed** for RF-062. Current spatial queries execute through a
separate terminal renderer.

### SQC-13: Event-driven automation

```csharp
options.Events.Triggers.AddTrigger<Order>("reserve_inventory", trigger => trigger
    .When(change =>
        change.Event == TableEvent.Create &&
        change.After.Status == OrderStatus.Pending)
    .Then((change, actions) => actions
        .Update<Product>(change.After.ProductId)
        .Increment(product => product.Stock, -change.After.Quantity)));
```

Expected command:

```surql
DEFINE EVENT OVERWRITE reserve_inventory ON TABLE order
WHEN $event = "CREATE" AND $after.status = $p0
THEN (UPDATE $after.product_id SET stock -= $after.quantity);
```

Status: **Proposed** for RF-071. The current trigger API accepts raw condition
and value strings; that implementation must not be carried forward as the typed
path. An explicitly named `WhenRaw`/`ThenRaw` escape hatch may remain.

## Contract-test rollout

Contract tests are enabled with their implementation slice instead of adding
passing stubs:

| Contract | First enabling task | Required verification |
|---|---|---|
| Inspection and rename | RF-010, RF-011 | Compile-time API tests; exact `SableCommand`; safe `ToString()` |
| Multi-model and subquery | RF-030, RF-033, RF-061 | Exact command and parameter tests |
| ACID/unit of work | RF-050 through RF-052 | Exact transaction plan plus failure-path integration |
| Built-in auth | RF-070 | Exact DDL, parameter/identifier validation, live integration |
| Hybrid RAG and memory | RF-060 | Text, vector, fusion, projection, paging tests |
| Graph RAG/context/knowledge | RF-061 | Traversal-source, depth, path, and projection tests |
| Live query | RF-063 | Command parity, cancellation, and backpressure tests |
| Recommendations | RF-060, RF-061 | Search-to-graph composition test |
| Geospatial | RF-062 | Predicate, distance projection/order, and paging tests |
| Event automation | RF-071 | Exact DDL, typed before/after values, and action tests |

No contract is considered enabled until both its public example compiles and its
generated `SableCommand` matches the executed command.

## Proposed decisions for Checkpoint A

1. Keep the current unit-of-work and explicit transaction public APIs. Do not
   add a second transaction-script DSL.
2. Replace the terminal `ISearchQuery<T>`, `IGraphQuery<T>`, and
   `ISpatialQuery<T>` surfaces rather than adapting them indefinitely.
3. Implement advanced read features as expressions/nodes in the normal
   `IQueryable<T>` pipeline.
4. Use a scoped, nonterminal search clause plus query-context score markers so
   numeric SurrealQL match indexes remain internal.
5. Use a typed `Traverse()` state with `Out<TEdge, TTarget>`,
   `In<TEdge, TTarget>`, and `Both<TEdge, TTarget>` as element-changing query
   operators.
6. Reuse the compiler's statement/expression nodes for typed access and event
   DDL; retain raw fragments only behind explicitly named escape hatches.

These choices intentionally spend the alpha breaking-change budget on one
coherent model instead of compatibility wrappers around disconnected builders.
