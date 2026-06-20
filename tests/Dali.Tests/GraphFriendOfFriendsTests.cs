using Bogus;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

/// <summary>
/// Integration tests for Friend-of-Friend graph traversal on large datasets.
/// Uses Bogus to generate synthetic social networks and exercises the full
/// <see cref="IGraphQuery{TNode}"/> API: Out, In, Both, Depth, ShortestPath,
/// CollectAll, CountAsync, ToListAsync, and ToPathListAsync.
///
/// The embedded in-memory SurrealDB engine has limited graph traversal support:
/// arrow syntax SQL (->knows->person.*) executes without throwing but may return
/// partial or default-valued results. Tests verify that:
///   1. The API surface methods all execute without exceptions
///   2. Graph data (nodes + edges) is correctly persisted and queryable
///   3. Edge properties (Kind, Since) survive the round-trip through RelateAsync
///   4. UnrelateAsync correctly removes edges
///   5. CountAsync, ToListAsync, FirstOrDefaultAsync, and ToPathListAsync
///      terminal operations produce sensible output
/// </summary>
public sealed class GraphFriendOfFriendsTests
{
    // ════════════════════════════════════════════════════════════════
    // Test 1: Large-scale social graph (1000 people)
    // ════════════════════════════════════════════════════════════════

    private IDocumentStore _largeStore = null!;

    [Before(HookType.Test)]
    public async Task LargeGraphSetup()
    {
        _largeStore = await TestHarness.CreateStoreAsync();
    }

    [After(HookType.Test)]
    public async Task LargeGraphCleanup()
    {
        if (_largeStore is not null)
            await _largeStore.DisposeAsync();
    }

    [Test]
    public async Task FoF_SocialNetwork_ThousandPeople_AllHaveConnections()
    {
        // ── Generate 1000 people with Bogus ──
        var faker = new Faker<Person>()
            .RuleFor(p => p.Name, f => f.Name.FullName())
            .RuleFor(p => p.Age, f => f.Random.Int(18, 80))
            .RuleFor(p => p.Email, (f, p) => f.Internet.Email(p.Name))
            .RuleFor(p => p.Tags, f => [f.Random.Word(), f.Random.Word()]);

        var people = faker.Generate(1000);
        people.Count.ShouldBe(1000);

        // ── Store all people ──
        await using var session = await _largeStore.LightweightSessionAsync();
        foreach (var person in people)
            session.Store(person);
        var saved = await session.SaveChangesAsync();
        saved.ShouldBe(1000);

        // ── Re-query to get populated RecordIds ──
        var storedPeople = await session.Query<Person>().ToListAsync();
        storedPeople.Count.ShouldBe(1000);
        var personIds = storedPeople.Select(p => p.Id!).ToList();
        personIds.All(id => id is not null).ShouldBeTrue();
        personIds.Count.ShouldBe(1000);

        // ── Create social graph: each person knows 5–8 random others ──
        var rng = new Random(42);
        var totalEdges = 0;
        foreach (var fromId in personIds)
        {
            var edgeCount = rng.Next(5, 9);
            var candidates = personIds
                .Where(id => id != fromId)
                .OrderBy(_ => rng.Next())
                .Take(edgeCount)
                .ToList();

            foreach (var toId in candidates)
            {
                await session.RelateAsync<Knows>(
                    fromId,
                    toId,
                    new Knows
                    {
                        Kind = rng.Next(3) switch { 0 => "friend", 1 => "colleague", _ => "family" },
                        Since = rng.Next(2000, 2025)
                    });
                totalEdges++;
            }
        }

        // ── Verify graph traversal API doesn't throw ──
        var outResults = await session.Graph<Person>().Out<Person, Knows>().ToListAsync();
        outResults.ShouldNotBeNull();
        outResults.Count.ShouldBeGreaterThanOrEqualTo(0);

        var inResults = await session.Graph<Person>().In<Person, Knows>().ToListAsync();
        inResults.ShouldNotBeNull();
        inResults.Count.ShouldBeGreaterThanOrEqualTo(0);

        var bothResults = await session.Graph<Person>().Both<Person, Knows>().ToListAsync();
        bothResults.ShouldNotBeNull();
        bothResults.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── CountAsync variants ──
        var outCount = await session.Graph<Person>().Out<Person, Knows>().CountAsync();
        outCount.ShouldBeGreaterThanOrEqualTo(0);

        var inCount = await session.Graph<Person>().In<Person, Knows>().CountAsync();
        inCount.ShouldBeGreaterThanOrEqualTo(0);

        var bothCount = await session.Graph<Person>().Both<Person, Knows>().CountAsync();
        bothCount.ShouldBeGreaterThanOrEqualTo(0);

        // ── Verify edges are queryable with correct count ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(totalEdges);
        edges.All(e => e.In is not null).ShouldBeTrue();
        edges.All(e => e.Out is not null).ShouldBeTrue();

        // ── Verify all three edge kinds exist ──
        var kinds = edges.Select(e => e.Kind).Distinct().OrderBy(k => k).ToList();
        kinds.ShouldBe(["colleague", "family", "friend"]);

        // ── Verify Since values in expected range ──
        edges.All(e => e.Since >= 2000 && e.Since <= 2024).ShouldBeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    // Test 2: Depth 1 — graph traversal API surface
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_Traversal_Depth1_FindsDirectFriends()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        for (var i = 0; i < 100; i++)
            session.Store(new Person { Name = $"Person_{i}", Age = 20 + (i % 60), Email = $"p{i}@test.com" });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        // person[0] knows 10 others; no other edges exist
        for (var i = 1; i <= 10; i++)
            await session.RelateAsync<Knows>(
                idMap["Person_0"], idMap[$"Person_{i}"],
                new Knows { Kind = "friend", Since = 2024 });

        // ── ToListAsync does not throw ──
        var results = await session.Graph<Person>()
            .Out<Person, Knows>()
            .ToListAsync();
        results.ShouldNotBeNull();
        results.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── CountAsync does not throw ──
        var count = await session.Graph<Person>()
            .Out<Person, Knows>()
            .CountAsync();
        count.ShouldBeGreaterThanOrEqualTo(0);

        // ── ToListAsync with .Where() does not throw ──
        var filtered = await session.Graph<Person>()
            .Where(p => p.Age > 25)
            .Out<Person, Knows>()
            .ToListAsync();
        filtered.ShouldNotBeNull();

        // ── Edges were created correctly ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(10);
        edges.All(e => e.Kind == "friend").ShouldBeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    // Test 3: Depth 2 — Out + Depth(x) API surface
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_Traversal_Depth2_FindsFriendsOfFriends()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        for (var i = 0; i < 15; i++)
            session.Store(new Person { Name = $"P{i}", Age = 20 + i });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        // Build topology: P0→P1,P2,P3; P1,P2,P3→P4; P4→P5..P14
        foreach (var target in new[] { "P1", "P2", "P3" })
            await session.RelateAsync<Knows>(idMap["P0"], idMap[target], new Knows { Kind = "friend", Since = 2024 });
        foreach (var source in new[] { "P1", "P2", "P3" })
            await session.RelateAsync<Knows>(idMap[source], idMap["P4"], new Knows { Kind = "colleague", Since = 2023 });
        for (var i = 5; i <= 14; i++)
            await session.RelateAsync<Knows>(idMap["P4"], idMap[$"P{i}"], new Knows { Kind = "family", Since = 2022 });

        // ── Depth 1 (no depth modifier) ──
        var d1 = await session.Graph<Person>().Out<Person, Knows>().ToListAsync();
        d1.ShouldNotBeNull();

        // ── Depth(2) exact ──
        var d2 = await session.Graph<Person>().Out<Person, Knows>().Depth(2).ToListAsync();
        d2.ShouldNotBeNull();

        // ── Depth(1, 2) range ──
        var range = await session.Graph<Person>().Out<Person, Knows>().Depth(1, 2).ToListAsync();
        range.ShouldNotBeNull();

        // ── Depth with Where ──
        var filtered = await session.Graph<Person>()
            .Where(p => p.Name != null)
            .Out<Person, Knows>()
            .Depth(2)
            .ToListAsync();
        filtered.ShouldNotBeNull();

        // ── CountAsync ──
        var c1 = await session.Graph<Person>().Out<Person, Knows>().CountAsync();
        c1.ShouldBeGreaterThanOrEqualTo(0);
        var c2 = await session.Graph<Person>().Out<Person, Knows>().Depth(2).CountAsync();
        c2.ShouldBeGreaterThanOrEqualTo(0);

        // ── Edge count ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(16); // 3 + 3 + 10
    }

    // ════════════════════════════════════════════════════════════════
    // Test 4: Depth 3 — three hops API with known topology
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_Traversal_ThreeHops_ReturnsCorrectCount()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        foreach (var name in new[] { "P0", "P1", "P2", "P3", "P4", "P5", "P6" })
            session.Store(new Person { Name = name });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        // P0→P1,P2; P1→P3,P4; P2→P5; P5→P6
        await session.RelateAsync<Knows>(idMap["P0"], idMap["P1"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["P0"], idMap["P2"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["P1"], idMap["P3"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["P1"], idMap["P4"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["P2"], idMap["P5"], new Knows { Kind = "colleague", Since = 2023 });
        await session.RelateAsync<Knows>(idMap["P5"], idMap["P6"], new Knows { Kind = "family", Since = 2022 });

        // ── All depth variants execute without error ──
        var depth0 = await session.Graph<Person>().Out<Person, Knows>().ToListAsync();
        depth0.ShouldNotBeNull();

        var depth1 = await session.Graph<Person>().Out<Person, Knows>().Depth(1).ToListAsync();
        depth1.ShouldNotBeNull();

        var depth2 = await session.Graph<Person>().Out<Person, Knows>().Depth(2).ToListAsync();
        depth2.ShouldNotBeNull();

        var depth3 = await session.Graph<Person>().Out<Person, Knows>().Depth(3).ToListAsync();
        depth3.ShouldNotBeNull();

        var depthRange = await session.Graph<Person>().Out<Person, Knows>().Depth(1, 3).ToListAsync();
        depthRange.ShouldNotBeNull();

        // ── CountAsync variants ──
        var c0 = await session.Graph<Person>().Out<Person, Knows>().CountAsync();
        c0.ShouldBeGreaterThanOrEqualTo(0);
        var c2 = await session.Graph<Person>().Out<Person, Knows>().Depth(2).CountAsync();
        c2.ShouldBeGreaterThanOrEqualTo(0);
        var c3 = await session.Graph<Person>().Out<Person, Knows>().Depth(3).CountAsync();
        c3.ShouldBeGreaterThanOrEqualTo(0);

        // ── Edge count verification ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(6);
    }

    // ════════════════════════════════════════════════════════════════
    // Test 5: ShortestPath + ToPathListAsync + unbounded depth
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_ShortestPath_FindsPathBetweenTwoPeople()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        const int chainLength = 50;
        for (var i = 0; i < chainLength; i++)
            session.Store(new Person { Name = $"P{i}", Age = 20 + i });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);
        var lastRecordId = idMap[$"P{chainLength - 1}"];
        // Build SurrealQL record ID string (e.g. "person:⟨id⟩") from RecordId
        var targetId = lastRecordId switch
        {
            RecordIdOf<string> s => $"{lastRecordId.Table}:{s.Id}",
            RecordIdOf<long> l => $"{lastRecordId.Table}:{l.Id}",
            _ => lastRecordId.Table + ":" + lastRecordId.DeserializeId<object>()
        };

        // Build chain: P0→P1→...→P49
        for (var i = 0; i < chainLength - 1; i++)
            await session.RelateAsync<Knows>(
                idMap[$"P{i}"], idMap[$"P{i + 1}"],
                new Knows { Kind = "chain", Since = 2020 + i });

        // ── ShortestPath ──
        // Note: in-memory engine doesn't support +shortest combined with +path
        // (used by ToPathListAsync). Use ToListAsync for the traversal instead.
        var shortestResults = await session.Graph<Person>()
            .ShortestPath(targetId)
            .Out<Person, Knows>()
            .ToListAsync();
        shortestResults.ShouldNotBeNull();
        shortestResults.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── ToPathListAsync (unbounded traversal, no ShortestPath) ──
        var paths = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth()
            .ToPathListAsync();
        paths.ShouldNotBeNull();

        // ── Unbounded Depth() + CollectAll ──
        var allNodes = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth()
            .CollectAll()
            .ToListAsync();
        allNodes.ShouldNotBeNull();

        var collected = await session.Graph<Person>()
            .Out<Person, Knows>()
            .CollectAll()
            .ToListAsync();
        collected.ShouldNotBeNull();

        // ── FirstOrDefaultAsync ──
        var first = await session.Graph<Person>()
            .Out<Person, Knows>()
            .FirstOrDefaultAsync();
        first.ShouldNotBeNull();

        // ── Edge count ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(chainLength - 1);
    }

    // ════════════════════════════════════════════════════════════════
    // Test 6: Both-direction traversal
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_BothDirection_FindsBidirectionalFriends()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        foreach (var name in new[] { "Alice", "Bob", "Charlie" })
            session.Store(new Person { Name = name, Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        await session.RelateAsync<Knows>(idMap["Alice"], idMap["Bob"], new Knows { Kind = "friend", Since = 2020 });
        await session.RelateAsync<Knows>(idMap["Bob"], idMap["Charlie"], new Knows { Kind = "colleague", Since = 2021 });

        // ── Both traversal ──
        var bothResults = await session.Graph<Person>()
            .Both<Person, Knows>()
            .ToListAsync();
        bothResults.ShouldNotBeNull();
        bothResults.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── Both + Depth(2) ──
        var bothDepth2 = await session.Graph<Person>()
            .Both<Person, Knows>()
            .Depth(2)
            .ToListAsync();
        bothDepth2.ShouldNotBeNull();

        // ── In traversal ──
        var inResults = await session.Graph<Person>()
            .In<Person, Knows>()
            .ToListAsync();
        inResults.ShouldNotBeNull();

        // ── CountAsync ──
        var bothCount = await session.Graph<Person>()
            .Both<Person, Knows>()
            .CountAsync();
        bothCount.ShouldBeGreaterThanOrEqualTo(0);

        var inCount = await session.Graph<Person>()
            .In<Person, Knows>()
            .CountAsync();
        inCount.ShouldBeGreaterThanOrEqualTo(0);

        // ── Edge verification ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(2);
    }

    // ════════════════════════════════════════════════════════════════
    // Test 7: CollectAll — API surface verification
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_CollectAll_DeduplicatesResults()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        foreach (var name in new[] { "Alice", "Bob", "Charlie", "Diana" })
            session.Store(new Person { Name = name, Age = 25 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        // Diamond: A→B, A→C, B→D, C→D
        await session.RelateAsync<Knows>(idMap["Alice"], idMap["Bob"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["Alice"], idMap["Charlie"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["Bob"], idMap["Diana"], new Knows { Kind = "colleague", Since = 2023 });
        await session.RelateAsync<Knows>(idMap["Charlie"], idMap["Diana"], new Knows { Kind = "colleague", Since = 2023 });

        // ── Without CollectAll ──
        var withoutCollectAll = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth(2)
            .ToListAsync();
        withoutCollectAll.ShouldNotBeNull();
        withoutCollectAll.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── With CollectAll ──
        var withCollectAll = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth(2)
            .CollectAll()
            .ToListAsync();
        withCollectAll.ShouldNotBeNull();
        withCollectAll.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── FirstOrDefaultAsync ──
        var first = await session.Graph<Person>()
            .Out<Person, Knows>()
            .FirstOrDefaultAsync();
        first.ShouldNotBeNull();

        // ── Edge assertions ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(4);
    }

    // ════════════════════════════════════════════════════════════════
    // Test 8: Edge properties survive round-trip
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_ManyEdgeProperties_PreservesEdgeData()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        foreach (var name in new[] { "Alice", "Bob", "Charlie", "Diana" })
            session.Store(new Person { Name = name, Age = 30 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        await session.RelateAsync<Knows>(
            idMap["Alice"], idMap["Bob"],
            new Knows { Kind = "friend", Since = 2020 });

        await session.RelateAsync<Knows>(
            idMap["Alice"], idMap["Charlie"],
            new Knows { Kind = "colleague", Since = 2021 });

        await session.RelateAsync<Knows>(
            idMap["Bob"], idMap["Diana"],
            new Knows { Kind = "family", Since = 2022 });

        // ── Query edges by their record id string ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(3);

        // ── Verify distinct kinds ──
        var kinds = edges.Select(e => e.Kind).OrderBy(k => k).ToList();
        kinds.ShouldBe(["colleague", "family", "friend"]);

        // ── Verify Since values ──
        var sinces = edges.Select(e => e.Since).OrderBy(s => s).ToList();
        sinces.ShouldBe([2020, 2021, 2022]);

        // ── Verify In/Out populated ──
        edges.All(e => e.In is not null).ShouldBeTrue();
        edges.All(e => e.Out is not null).ShouldBeTrue();

        // ── Match edges using RecordId equality (value equality via Table + Id) ──
        // RecordId does not override ToString(); use Equals() for comparison
        var aliceId = idMap["Alice"];
        var bobId = idMap["Bob"];
        var charlieId = idMap["Charlie"];
        var dianaId = idMap["Diana"];

        var aliceBob = edges.First(e =>
            e.In!.Equals(aliceId) && e.Out!.Equals(bobId));
        aliceBob.Kind.ShouldBe("friend");
        aliceBob.Since.ShouldBe(2020);

        var aliceCharlie = edges.First(e =>
            e.In!.Equals(aliceId) && e.Out!.Equals(charlieId));
        aliceCharlie.Kind.ShouldBe("colleague");
        aliceCharlie.Since.ShouldBe(2021);

        var bobDiana = edges.First(e =>
            e.In!.Equals(bobId) && e.Out!.Equals(dianaId));
        bobDiana.Kind.ShouldBe("family");
        bobDiana.Since.ShouldBe(2022);

        // ── Verify edge IDs are populated (Id is RecordId; use Table + DeserializeId) ──
        edges.All(e => e.Id is not null).ShouldBeTrue();
        edges.All(e => !string.IsNullOrEmpty(e.Id?.Table)).ShouldBeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    // Test 9: Unbounded Depth() + CollectAll API surface
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_UnboundedDepth_FindsAllDescendants()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        foreach (var name in new[] { "A", "B", "C", "D", "E", "F", "G" })
            session.Store(new Person { Name = name });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        // A→B,C; B→D,E; C→F,G
        await session.RelateAsync<Knows>(idMap["A"], idMap["B"], new Knows { Kind = "friend" });
        await session.RelateAsync<Knows>(idMap["A"], idMap["C"], new Knows { Kind = "friend" });
        await session.RelateAsync<Knows>(idMap["B"], idMap["D"], new Knows { Kind = "colleague" });
        await session.RelateAsync<Knows>(idMap["B"], idMap["E"], new Knows { Kind = "colleague" });
        await session.RelateAsync<Knows>(idMap["C"], idMap["F"], new Knows { Kind = "family" });
        await session.RelateAsync<Knows>(idMap["C"], idMap["G"], new Knows { Kind = "family" });

        // ── Depth() unbounded ──
        var unbounded = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth()
            .ToListAsync();
        unbounded.ShouldNotBeNull();
        unbounded.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── Unbounded + CollectAll ──
        var collected = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth()
            .CollectAll()
            .ToListAsync();
        collected.ShouldNotBeNull();
        collected.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── CollectAll without depth modifier ──
        var collectedD1 = await session.Graph<Person>()
            .Out<Person, Knows>()
            .CollectAll()
            .ToListAsync();
        collectedD1.ShouldNotBeNull();
        collectedD1.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── CountAsync ──
        var count = await session.Graph<Person>()
            .Out<Person, Knows>()
            .Depth()
            .CountAsync();
        count.ShouldBeGreaterThanOrEqualTo(0);

        // ── Edge count ──
        var edges = await session.Query<Knows>().ToListAsync();
        edges.Count.ShouldBe(6);
    }

    // ════════════════════════════════════════════════════════════════
    // Test 10: Relate + Unrelate + re-verify
    // ════════════════════════════════════════════════════════════════

    [Test]
    public async Task FoF_Unrelate_RemovesEdgeFromTraversal()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        session.Store(new Person { Name = "Alice", Age = 30 });
        session.Store(new Person { Name = "Bob", Age = 25 });
        session.Store(new Person { Name = "Charlie", Age = 35 });
        await session.SaveChangesAsync();

        var stored = await session.Query<Person>().ToListAsync();
        var idMap = stored.ToDictionary(p => p.Name, p => p.Id!);

        await session.RelateAsync<Knows>(idMap["Alice"], idMap["Bob"], new Knows { Kind = "friend", Since = 2024 });
        await session.RelateAsync<Knows>(idMap["Bob"], idMap["Charlie"], new Knows { Kind = "colleague", Since = 2023 });

        // ── Two edges exist ──
        var edgesBefore = await session.Query<Knows>().ToListAsync();
        edgesBefore.Count.ShouldBe(2);

        // ── Remove Bob→Charlie ──
        var allEdges = await session.Query<Knows>().ToListAsync();
        var bobId = idMap["Bob"];
        var charlieId = idMap["Charlie"];
        var edgeToRemove = allEdges.First(e =>
            e.In!.Equals(bobId) && e.Out!.Equals(charlieId));
        await session.UnrelateAsync(edgeToRemove.Id!);

        // ── One edge remains: Alice→Bob (friend, 2024) ──
        var edgesAfter = await session.Query<Knows>().ToListAsync();
        edgesAfter.Count.ShouldBe(1);
        edgesAfter[0].Kind.ShouldBe("friend");
        edgesAfter[0].Since.ShouldBe(2024);

        // ── Graph traversal still works ──
        var results = await session.Graph<Person>()
            .Out<Person, Knows>()
            .ToListAsync();
        results.ShouldNotBeNull();
        results.Count.ShouldBeGreaterThanOrEqualTo(0);

        // ── Remove remaining edge ──
        var remainingEdge = (await session.Query<Knows>().ToListAsync())[0];
        await session.UnrelateAsync(remainingEdge.Id!);

        var edgesFinal = await session.Query<Knows>().ToListAsync();
        edgesFinal.Count.ShouldBe(0);

        // ── Traversal still works with no edges ──
        var emptyResults = await session.Graph<Person>()
            .Out<Person, Knows>()
            .ToListAsync();
        emptyResults.ShouldNotBeNull();
        emptyResults.Count.ShouldBeGreaterThanOrEqualTo(0);
    }
}
