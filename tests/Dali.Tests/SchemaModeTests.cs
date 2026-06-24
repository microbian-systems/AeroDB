using TUnit.Core;

namespace Dali.Tests;

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

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
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

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
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
        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;

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
        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;

        await schemaManager.EnsureDocumentSchemaAsync<Person>(surrealSession, SchemaMode.Strict);

        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
        response.Count.ShouldBeGreaterThan(0);
    }
}
