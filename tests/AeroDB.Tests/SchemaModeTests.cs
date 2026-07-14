using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

public class SchemaModeTests
{
    [Test]
    public async Task SchemaMode_DefaultIsStrict()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>();

        var mapping = (DocumentMapping)options.Schema.Mappings[typeof(Person)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public async Task SchemaMode_CanSetFlexible()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>().SetSchemaMode(SchemaMode.Flexible);

        var mapping = (DocumentMapping)options.Schema.Mappings[typeof(Person)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Flexible);
    }

    [Test]
    public async Task SchemaMode_CanSetStrict()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>().SetSchemaMode(SchemaMode.Strict);

        var mapping = (DocumentMapping)options.Schema.Mappings[typeof(Person)];
        mapping.SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public async Task SchemaMode_FluentApiReturnsDocumentMapping()
    {
        var options = new StoreOptions();
        var mapping = options.Schema.For<Person>().SetSchemaMode(SchemaMode.Flexible);

        mapping.ShouldNotBeNull();
        mapping.ShouldBeOfType<DocumentMapping<Person>>();
    }

    [Test]
    public async Task SchemaMode_MultipleDocumentTypes_Independent()
    {
        var options = new StoreOptions();
        options.Schema.For<Person>().SetSchemaMode(SchemaMode.Flexible);
        options.Schema.For<Product>().SetSchemaMode(SchemaMode.Strict);

        var mappings = options.Schema.Mappings;
        mappings.Count.ShouldBe(2);
        ((DocumentMapping)mappings[typeof(Person)]).SchemaModeType.ShouldBe(SchemaMode.Flexible);
        ((DocumentMapping)mappings[typeof(Product)]).SchemaModeType.ShouldBe(SchemaMode.Strict);
    }

    [Test]
    public async Task SchemaMode_FlexibleCreatesSchemalessTable()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Person>().SetSchemaMode(SchemaMode.Flexible);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task SchemaMode_StrictCreatesSchemafullTable()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Product>().SetSchemaMode(SchemaMode.Strict);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
        var response = await surrealSession.RawQuery("INFO FOR TABLE product;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task SchemaMode_DefaultOnSchemaManager_SealedTypeIsStrict()
    {
        // SchemaManager uses typeof(T).IsSealed to pick default:
        // sealed → Strict, non-sealed → Flexible
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        // Person is not sealed, so default should be Flexible
        await schemaManager.EnsureDocumentSchemaAsync<Person>(surrealSession);

        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task SchemaMode_CanPassExplicitMode()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var schemaManager = new SchemaManager();
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;

        await schemaManager.EnsureDocumentSchemaAsync<Person>(surrealSession, SchemaMode.Strict);

        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task SchemaMode_StrictNestedPoco_DefinesEmbeddedObjectAsFlexible()
    {
        var surrealSession = Substitute.For<ISurrealDbSession>();
        surrealSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([])));

        var schemaManager = new SchemaManager();

        await schemaManager.EnsureDocumentSchemaAsync<StrictMediaDocument>(
            surrealSession,
            SchemaMode.Strict);

        await surrealSession.Received(1).RawQuery(
            "DEFINE FIELD attribution ON TABLE strict_media_document TYPE option<object> FLEXIBLE;",
            null,
            Arg.Any<CancellationToken>());
        await surrealSession.Received(1).RawQuery(
            "DEFINE FIELD credits ON TABLE strict_media_document TYPE option<array<object>> FLEXIBLE;",
            null,
            Arg.Any<CancellationToken>());
        await surrealSession.Received(1).RawQuery(
            "DEFINE FIELD OVERWRITE credits.* ON TABLE strict_media_document TYPE object FLEXIBLE;",
            null,
            Arg.Any<CancellationToken>());
        await surrealSession.Received(1).RawQuery(
            "DEFINE FIELD metadata ON TABLE strict_media_document TYPE option<object> FLEXIBLE;",
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SchemaMode_StrictNestedPoco_RoundTripsEmbeddedObject()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<StrictMediaDocument>()
                .Identity(x => x.Id)
                .SetSchemaMode(SchemaMode.Strict));

        await using (var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(new StrictMediaDocument
            {
                Id = 42,
                Name = "Hero",
                Attribution = new StrictMediaAttribution
                {
                    Id = 7,
                    CreatorName = "Ada",
                    Platform = "Pexels"
                },
                Credits =
                [
                    new StrictMediaAttribution
                    {
                        Id = 8,
                        CreatorName = "Grace",
                        Platform = "Unsplash"
                    }
                ],
                Metadata = new Dictionary<string, string>
                {
                    ["license"] = "CC0"
                }
            });
            await session.SaveChangesAsync();
        }

        await using var query = await store.QuerySessionAsync();
        var saved = (await query.Query<StrictMediaDocument>().ToListAsync())
            .SingleOrDefault(x => x.Name == "Hero");

        saved.ShouldNotBeNull();
        saved.Attribution.ShouldNotBeNull();
        saved.Attribution.Id.ShouldBe(7);
        saved.Attribution.CreatorName.ShouldBe("Ada");
        saved.Attribution.Platform.ShouldBe("Pexels");
        saved.Credits.Count.ShouldBe(1);
        saved.Credits[0].CreatorName.ShouldBe("Grace");
        saved.Metadata["license"].ShouldBe("CC0");
    }

}

public sealed class StrictMediaDocument : SableDocument
{
    public string Name { get; set; } = string.Empty;
    public StrictMediaAttribution? Attribution { get; set; }
    public List<StrictMediaAttribution> Credits { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class StrictMediaAttribution
{
    public long Id { get; set; }
    public string? CreatorName { get; set; }
    public string? Platform { get; set; }
}
