using Bogus;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class TypedIdentityStorageTests
{
    [Test]
    public async Task LongIdentityIsStoredAsNativeNumericRecordKey(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "typed_identity_storage",
            options => options.Schema
                .For<Product>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible),
            ct);

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None },
            ct);

        await session.ExecuteSqlAsync("DELETE product;", null, ct);

        var product = new Faker<Product>("en")
            .UseSeed(2207)
            .RuleFor(x => x.Id, _ => 1_501_688_860_171_780_096L)
            .RuleFor(x => x.Name, faker => faker.Commerce.ProductName())
            .RuleFor(x => x.Price, faker => faker.Finance.Amount(25, 500, 2))
            .RuleFor(x => x.Stock, faker => faker.Random.Int(1, 20))
            .Generate();

        session.Store(product);
        await session.SaveChangesAsync(ct);

        var probes = await session.RawQueryAsync<RecordIdTypeProbe>(
            """
            SELECT
                record::id(id) AS value,
                type::is_int(record::id(id)) AS is_numeric
            FROM product;
            """,
            null,
            ct);

        probes.Count.ShouldBe(1);
        probes[0].IsNumeric.ShouldBeTrue();
        probes[0].Value.ShouldBe(product.Id);

        await using var query = await store.QuerySessionAsync(ct);
        (await query.CheckExistsAsync<Product>(product.Id, ct)).ShouldBeTrue();
        (await query.LoadAsync<Product>(product.Id, ct)).ShouldNotBeNull();
        var loaded = await query.LoadManyAsync<Product>([product.Id], ct);
        loaded.Count.ShouldBe(1);
        loaded[0].Id.ShouldBe(product.Id);
    }

    [Test]
    public async Task NumericLookingStringIdentityRemainsAStringRecordKey(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "typed_identity_storage",
            options => options.Schema
                .For<ExternalProduct>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible),
            ct);
        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None },
            ct);

        await session.ExecuteSqlAsync("DELETE external_product;", null, ct);

        var document = new Faker<ExternalProduct>("en")
            .UseSeed(2208)
            .RuleFor(x => x.Id, _ => "1501688860171780096")
            .RuleFor(x => x.Name, faker => faker.Commerce.ProductName())
            .Generate();

        session.Store(document);
        await session.SaveChangesAsync(ct);

        var probes = await session.RawQueryAsync<StringRecordIdTypeProbe>(
            """
            SELECT
                record::id(id) AS value,
                type::is_string(record::id(id)) AS is_string
            FROM external_product;
            """,
            null,
            ct);

        probes.Count.ShouldBe(1);
        probes[0].IsString.ShouldBeTrue();
        probes[0].Value.ShouldBe(document.Id);
    }

    [Test]
    public async Task BulkInsertPreservesNativeNumericRecordKeys(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "typed_identity_storage",
            options => options.Schema
                .For<Product>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Flexible),
            ct);
        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None },
            ct);

        await session.ExecuteSqlAsync("DELETE product;", null, ct);

        var products = new Faker<Product>("en")
            .UseSeed(2209)
            .RuleFor(x => x.Id, faker => 1_501_688_860_171_780_100L + faker.IndexFaker)
            .RuleFor(x => x.Name, faker => faker.Commerce.ProductName())
            .RuleFor(x => x.Price, faker => faker.Finance.Amount(25, 500, 2))
            .RuleFor(x => x.Stock, faker => faker.Random.Int(1, 20))
            .Generate(3);

        (await session.BulkInsertAsync(products, batchSize: 2, ct)).ShouldBe(products.Count);

        var probes = await session.RawQueryAsync<RecordIdTypeProbe>(
            """
            SELECT
                record::id(id) AS value,
                type::is_int(record::id(id)) AS is_numeric
            FROM product;
            """,
            null,
            ct);

        probes.Count.ShouldBe(products.Count);
        probes.ShouldAllBe(probe => probe.IsNumeric);
        probes.Select(probe => probe.Value).ShouldBe(
            products.Select(product => product.Id),
            ignoreOrder: true);
    }

    private sealed class RecordIdTypeProbe
    {
        public long Value { get; set; }
        public bool IsNumeric { get; set; }
    }

    private sealed class StringRecordIdTypeProbe
    {
        public string Value { get; set; } = string.Empty;
        public bool IsString { get; set; }
    }
}
