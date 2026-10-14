namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class RagSearchRegressionTests
{
    [Test]
    public async Task HybridReturnsFusedRecordsAndHonorsTake(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        var results = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "laptop")
            .WithVector(x => x.Embedding, [1f, 0f, 0f])
            .Take(2)
            .FuseAsync(rrfK: 1, rrfLimit: 5, ct);

        results.Count.ShouldBe(2);
        results[0].Id.ShouldBe("laptop-2");
        // Ranks 2 and 1 with k = 1: 1/(1+2) + 1/(1+1).
        results[0].RrfScore.ShouldBe(5d / 6d, 0.000001);
        results.ShouldAllBe(x => !string.IsNullOrEmpty(x.Body));
    }

    [Test]
    public async Task HybridPaginationPreservesFusionRank(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        var all = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "laptop")
            .WithVector(x => x.Embedding, [1f, 0f, 0f])
            .Take(4)
            .ToListAsync(ct);
        var page = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "laptop")
            .WithVector(x => x.Embedding, [1f, 0f, 0f])
            .Skip(1)
            .Take(2)
            .ToListAsync(ct);

        all.Count.ShouldBe(4);
        page.Select(x => x.Id).ShouldBe(all.Skip(1).Take(2).Select(x => x.Id));
    }

    [Test]
    public async Task HybridOrderingSeesFullCandidateUnion(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        var results = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "laptop")
            .WithVector(x => x.Embedding, [1f, 0f, 0f])
            .OrderBy(x => x.Id, descending: true)
            .Take(2)
            .FuseAsync(rrfK: 60, rrfLimit: 1, ct);

        // Text rank 1 is laptop-1, vector rank 1 is laptop-2. An ordering
        // override must neither change those ranks nor truncate their union.
        results.Select(x => x.Id).ShouldBe(new[] { "laptop-2", "laptop-1" });
    }

    [Test]
    public async Task HybridFiltersBothCandidateBranches(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        var results = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "laptop")
            .WithVector(x => x.Embedding, [1f, 0f, 0f])
            .Where(x => x.Id != "laptop-2")
            .Take(4)
            .FuseAsync(rrfK: 60, rrfLimit: 5, ct);

        results.Select(x => x.Id).ShouldBe(
            new[] { "laptop-1", "forest-1", "river-1" }, ignoreOrder: true);
    }

    [Test]
    public async Task SearchErrorsAreNotReportedAsEmptyResults(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        // No full-text index exists on the identity field.
        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            query.Search<RagSearchDocument>()
                .MatchText(x => (object)x.Id, "laptop")
                .ToListAsync(ct));

        exception.Message.ShouldContain("SurrealDB search query error");
    }

    [Test]
    public async Task EmptySearchRemainsAnEmptyResult(CancellationToken ct)
    {
        await using var store = await CreateSearchStoreAsync(ct);
        await using var query = await store.QuerySessionAsync(ct);

        var results = await query.Search<RagSearchDocument>()
            .MatchText(x => (object)x.Body, "unmatchedword")
            .ToListAsync(ct);

        results.ShouldBeEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NumericIdentityPaginationUsesNativeRecordKey(bool descending, CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "rag_identity_ordering", options => options.Schema.For<Product>()
                .Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible), ct);
        await using var session = await store.LightweightSessionAsync(ct);
        await session.ExecuteSqlAsync("DELETE product;", null, ct);
        foreach (var id in new long[] { 30, 2, 10 })
            session.Store(new Product { Id = id, Name = $"Product {id}" });
        await session.SaveChangesAsync(ct);

        await using var query = await store.QuerySessionAsync(ct);
        var ordered = descending
            ? query.Query<Product>().OrderByDescending(x => x.Id)
            : query.Query<Product>().OrderBy(x => x.Id);
        var page = ordered.Skip(1).Take(2);
        page.ToCommand().CommandText.ShouldContain(descending ? "ORDER BY id DESC" : "ORDER BY id ASC");
        var results = await page.ToListAsync(ct);

        results.Select(x => x.Id).ShouldBe(descending ? new long[] { 10, 2 } : [10, 30]);
        // Key extraction must remain unchanged in predicates.
        var filtered = await query.Query<Product>().Where(x => x.Id > 2).ToListAsync(ct);
        filtered.Select(x => x.Id).ShouldBe(new long[] { 10, 30 }, ignoreOrder: true);
    }

    [Test]
    public async Task StringIdentityPaginationUsesLexicalRecordKey(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "rag_identity_ordering", options => options.Schema.For<ExternalProduct>()
                .Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible), ct);
        await using var session = await store.LightweightSessionAsync(ct);
        await session.ExecuteSqlAsync("DELETE external_product;", null, ct);
        foreach (var id in new[] { "z", "2", "10" })
            session.Store(new ExternalProduct { Id = id, Name = $"Product {id}" });
        await session.SaveChangesAsync(ct);

        await using var query = await store.QuerySessionAsync(ct);
        var page = query.Query<ExternalProduct>().OrderBy(x => x.Id).Skip(1).Take(1);
        page.ToCommand().CommandText.ShouldContain("ORDER BY id ASC");
        var results = await page.ToListAsync(ct);
        results.Select(x => x.Id).ShouldBe(new[] { "2" });
    }

    [Test]
    public async Task MappedIdentityThenByTranslatesToNativeRecordKey(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "rag_mapped_identity", options => options.Schema.For<RagMappedDocument>()
                .Identity(x => x.Key).SetSchemaMode(SchemaMode.Flexible), ct);
        await using var query = await store.QuerySessionAsync(ct);
        var ordered = query.Query<RagMappedDocument>()
            .Where(x => x.Key > 2)
            .OrderBy(x => x.Rank).ThenByDescending(x => x.Key).Take(3);
        // Inspect translation independently of the pre-existing schema defect:
        // non-Id identities are incorrectly defined as required ordinary fields.
        var command = ordered.ToCommand();
        command.CommandText.ShouldContain("ORDER BY rank ASC, id DESC");
        command.CommandText.ShouldContain("WHERE <int> meta::id(id) > $p0");
        command.Parameters["p0"].ShouldBe(2L);
    }

    [Test]
    public async Task ConventionalGuidIdentityOrderingUsesNativeRecordKey(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "rag_guid_identity", options => options.Schema.For<SessionDocument>()
                .SetSchemaMode(SchemaMode.Flexible), ct);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await using var session = await store.LightweightSessionAsync(ct);
        await session.ExecuteSqlAsync("DELETE session_document;", null, ct);
        session.Store(new SessionDocument { Id = second, Subject = "Second" });
        session.Store(new SessionDocument { Id = first, Subject = "First" });
        await session.SaveChangesAsync(ct);

        await using var query = await store.QuerySessionAsync(ct);
        var ordered = query.Query<SessionDocument>().OrderBy(x => x.Id);
        ordered.ToCommand().CommandText.ShouldContain("ORDER BY id ASC");
        var results = await ordered.ToListAsync(ct);
        results.Select(x => x.Id).ShouldBe(new[] { first, second });
    }

    private static async Task<IDocumentStore> CreateSearchStoreAsync(CancellationToken ct)
    {
        // Isolate index state across tests and repeated runs, while retaining
        // each database for inspecting a failure.
        var store = await ServerTestHarness.CreateStoreAsync("rag_search_" + Guid.NewGuid().ToString("N"), options =>
        {
            options.Schema.Analyzers.DefineAnalyzer(Search.Analyzer.Simple);
            options.Schema.For<RagSearchDocument>()
                .Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible)
                .FullTextIndex(x => x.Body, Search.Analyzer.Simple)
                .HnswIndex(x => x.Embedding, 3, Search.Distance.Cosine);
        }, ct);
        try
        {
            await using var session = await store.LightweightSessionAsync(ct);
            session.Store(new RagSearchDocument { Id = "laptop-1", Body = "laptop", Embedding = [0f, 1f, 0f] });
            session.Store(new RagSearchDocument { Id = "laptop-2", Body = "laptop forest river wolves", Embedding = [1f, 0f, 0f] });
            session.Store(new RagSearchDocument { Id = "forest-1", Body = "forest river", Embedding = [0.9f, 0.1f, 0f] });
            session.Store(new RagSearchDocument { Id = "river-1", Body = "river bridge", Embedding = [0.5f, 0.5f, 0f] });
            await session.SaveChangesAsync(ct);
            return store;
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }
}

public sealed class RagSearchDocument
{
    public string Id { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = [];
    public double RrfScore { get; set; }
}

public sealed class RagMappedDocument
{
    public long Key { get; set; }
    public int Rank { get; set; }
}
