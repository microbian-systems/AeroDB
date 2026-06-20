// ============================================================
// SurrealDB EF Core LINQ Provider
// Usage Examples — Document · Graph · SQL
// ============================================================

using SurrealEFCore.Core;
using SurrealEFCore.Extensions;
using SurrealEFCore.Infrastructure;

// ─────────────────────────────────────────────────────────────
// Bootstrap
// ─────────────────────────────────────────────────────────────

var opts = new SurrealDbOptions
{
    Url       = "http://localhost:8000",
    Namespace = "social",
    Database  = "app",
    Username  = "root",
    Password  = "root"
};

await using var ctx = new SurrealContext(opts);

// ═════════════════════════════════════════════════════════════
// 1 ▸ DOCUMENT MODE — Standard LINQ → SurrealQL
// ═════════════════════════════════════════════════════════════

Console.WriteLine("━━━ DOCUMENT QUERIES ━━━");

// 1a. Simple WHERE + ORDER BY + TAKE
//   → SELECT * FROM person WHERE age >= 18 ORDER BY name ASC LIMIT 10
var adults = await ctx.People
    .Where(p => p.Age >= 18)
    .OrderBy(p => p.Name)
    .Take(10)
    .ToListAsync();

// 1b. String method: Contains
//   → SELECT * FROM person WHERE string::contains(email, '@acme.com')
var acmeUsers = await ctx.People
    .Where(p => p.Email.Contains("@acme.com"))
    .ToListAsync();

// 1c. Projection — anonymous type → named columns
//   → SELECT name, age FROM person WHERE age > 21
var summary = await ctx.People
    .Where(p => p.Age > 21)
    .Select(p => new { p.Name, p.Age })
    .ToListAsync();

// 1d. Compound predicate
//   → SELECT * FROM person WHERE (age >= 18) AND (string::contains(name, 'A'))
var filtered = await ctx.People
    .Where(p => p.Age >= 18 && p.Name.Contains("A"))
    .ToListAsync();

// 1e. Skip + Take (pagination)
//   → SELECT * FROM person LIMIT 20 START 40
var page3 = await ctx.People
    .OrderBy(p => p.Name)
    .Skip(40)
    .Take(20)
    .ToListAsync();

// 1f. Tag membership (List<string>.Contains → CONTAINS)
//   → SELECT * FROM person WHERE tags CONTAINS 'admin'
var admins = await ctx.People
    .Where(p => p.Tags.Contains("admin"))
    .ToListAsync();

// 1g. CRUD
var alice = await ctx.People.AddAsync(new Person
{
    Name  = "Alice",
    Age   = 30,
    Email = "alice@example.com",
    Tags  = new() { "admin", "beta" }
});

var found = await ctx.People.FindAsync(alice.Id);

alice.Age = 31;
await ctx.People.UpdateAsync(alice);

await ctx.People.DeleteAsync(alice.Id);


// ═════════════════════════════════════════════════════════════
// 2 ▸ GRAPH MODE — Edge RELATE + Traversal
// ═════════════════════════════════════════════════════════════

Console.WriteLine("\n━━━ GRAPH QUERIES ━━━");

// 2a. Create nodes
var alicePerson = await ctx.People.UpsertAsync(new Person
    { Id = "person:alice", Name = "Alice", Age = 30, Email = "alice@ex.com" });

var bobPerson = await ctx.People.UpsertAsync(new Person
    { Id = "person:bob", Name = "Bob", Age = 25, Email = "bob@ex.com" });

var laptop = await ctx.Products.UpsertAsync(new Product
    { Id = "product:laptop", Name = "Laptop Pro", Price = 1299m, Category = "Electronics" });

// 2b. RELATE: Alice KNOWS Bob
//   → RELATE person:alice->knows->person:bob CONTENT {...}
await ctx.Graph.RelateAsync<Knows>(
    from: "person:alice",
    to:   "person:bob",
    edge: new Knows { Since = DateTimeOffset.UtcNow, Strength = 0.9 });

// 2c. RELATE: Alice PURCHASED Laptop
//   → RELATE person:alice->purchased->product:laptop CONTENT {...}
await ctx.Graph.RelateAsync<Purchased>(
    from: "person:alice",
    to:   "product:laptop",
    edge: new Purchased { Quantity = 1, Total = 1299m, At = DateTimeOffset.UtcNow });

// 2d. Traverse out: who does Alice know?
//   → SELECT ->knows->person.* FROM person:alice
var aliceFriends = await ctx.Graph.TraverseOutAsync<Knows, Person>(
    from: "person:alice");

// 2e. Traverse out with filter
//   → SELECT ->knows->person.* FROM person:alice WHERE age > 18
var adultFriends = await ctx.Graph.TraverseOutAsync<Knows, Person>(
    from:        "person:alice",
    whereClause: "age > 18");

// 2f. Traverse inbound: who knows Bob?
//   → SELECT <-knows<-person.* FROM person:bob
var bobKnowers = await ctx.Graph.TraverseInAsync<Knows, Person>(
    to: "person:bob");

// 2g. Multi-hop: friends of friends (depth 2)
//   → SELECT ->knows->person->knows->person.* FROM person:alice
var fof = await ctx.Graph.TraverseOutAsync<Knows, Person>(
    from:  "person:alice",
    depth: 2);

// 2h. LINQ-style traversal via extension method
//   → SELECT ->knows->person.* FROM person WHERE name = 'Alice'
var linqTraverse = await ctx.People
    .Where(p => p.Name == "Alice")
    .Traverse<Knows, Person>("out")
    .Where(p => p.Age > 18)
    .ToListAsync();

Console.WriteLine($"Alice's friends: {aliceFriends.Count}");


// ═════════════════════════════════════════════════════════════
// 3 ▸ SQL / ANALYTIC MODE — Raw SurrealQL
// ═════════════════════════════════════════════════════════════

Console.WriteLine("\n━━━ SQL / ANALYTIC QUERIES ━━━");

// 3a. Raw SurrealQL with full power
var raw = await ctx.QueryAsync<Person>("""
    SELECT *
    FROM person
    WHERE age > 21
    ORDER BY age DESC
    LIMIT 5;
""");

// 3b. Aggregate + math functions
var stats = await ctx.QueryAsync<Dictionary<string, object>>("""
    SELECT
        math::mean(age)  AS avg_age,
        math::min(age)   AS min_age,
        math::max(age)   AS max_age,
        count()          AS total
    FROM person
    GROUP ALL;
""");

// 3c. Full-text search (SurrealDB native)
var search = await ctx.QueryAsync<Person>("""
    SELECT *, search::score() AS relevance
    FROM person
    WHERE name @@ 'Ali'
    ORDER BY relevance DESC;
""");

// 3d. Subquery + nested SELECT
var richPeople = await ctx.QueryAsync<Person>("""
    SELECT *
    FROM person
    WHERE id IN (
        SELECT in FROM purchased WHERE total > 500
    );
""");

// 3e. Live query token (subscriptions)
var liveQuery = await ctx.QueryAsync<Dictionary<string, object>>("""
    LIVE SELECT * FROM person WHERE age > 18;
""");
var liveQueryId = liveQuery.FirstOrDefault()?["id"];
Console.WriteLine($"Live query token: {liveQueryId}");

// 3f. Raw escape hatch inside LINQ chain
var hybridQuery = await ctx.People
    .Where(p => p.Age > 18)
    .Raw("tags CONTAINS 'vip' AND array::len(tags) > 1")
    .OrderByDescending(p => p.Age)
    .Take(5)
    .ToListAsync();

// 3g. SurrealQL DEFINE / schema management
await ctx.ExecuteRawAsync("""
    DEFINE TABLE person SCHEMAFULL;
    DEFINE FIELD name  ON person TYPE string;
    DEFINE FIELD age   ON person TYPE int;
    DEFINE FIELD email ON person TYPE string
        ASSERT string::is::email($value);
    DEFINE INDEX person_email ON person FIELDS email UNIQUE;
    DEFINE ANALYZER english TOKENIZERS blank,class FILTERS lowercase,snowball(english);
    DEFINE INDEX person_name_search ON person FIELDS name SEARCH ANALYZER english BM25;
""");


// ═════════════════════════════════════════════════════════════
// 4 ▸ TRANSACTIONS
// ═════════════════════════════════════════════════════════════

Console.WriteLine("\n━━━ TRANSACTIONS ━━━");

var result = await ctx.TransactAsync(async db =>
{
    var buyer = await db.People.AddAsync(new Person
        { Name = "Charlie", Age = 28, Email = "charlie@ex.com" });

    await db.Graph.RelateAsync<Purchased>(
        from: buyer.Id,
        to:   "product:laptop",
        edge: new Purchased { Quantity = 2, Total = 2598m, At = DateTimeOffset.UtcNow });

    return buyer;
});

Console.WriteLine($"Transaction created person: {result.Id}");


// ═════════════════════════════════════════════════════════════
// 5 ▸ QUERY TRANSLATION OUTPUT
// Demonstrates what SurrealQL gets emitted for each LINQ expr
// ═════════════════════════════════════════════════════════════

Console.WriteLine("\n━━━ TRANSLATION PREVIEW ━━━");

void PrintQuery(string label, string surql) =>
    Console.WriteLine($"\n[{label}]\n  {surql}");

// These would be emitted by the visitor — shown for reference:
PrintQuery("Simple filter",
    "SELECT * FROM person WHERE age >= 18");

PrintQuery("Compound filter",
    "SELECT * FROM person WHERE (age >= 18) AND (string::contains(name, 'A'))");

PrintQuery("Projection",
    "SELECT name, age FROM person WHERE age > 21");

PrintQuery("Pagination",
    "SELECT * FROM person ORDER BY name ASC LIMIT 20 START 40");

PrintQuery("Graph traverse out",
    "SELECT ->knows->person.* FROM person WHERE name = 'Alice'");

PrintQuery("Graph traverse in",
    "SELECT <-knows<-person.* FROM person:bob");

PrintQuery("Multi-hop (depth 2)",
    "SELECT ->knows->person->knows->person.* FROM person:alice");

PrintQuery("String contains",
    "SELECT * FROM person WHERE string::contains(email, '@acme.com')");

PrintQuery("Tag membership",
    "SELECT * FROM person WHERE tags CONTAINS 'admin'");

PrintQuery("Raw escape",
    "SELECT * FROM person WHERE (age > 18) AND (/* raw */ tags CONTAINS 'vip' AND array::len(tags) > 1) ORDER BY age DESC LIMIT 5");
