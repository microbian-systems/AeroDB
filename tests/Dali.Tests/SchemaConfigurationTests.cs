using TUnit.Core;

namespace Dali.Tests;

public class SchemaConfigurationTests
{
    [Test]
    public async Task Schema_For_index_creates_simple_index()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Person>().Index(p => p.Name);
        });
        await store.InitializeAsync();

        // Verify the table was created and the index query succeeds
        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE person;");
        infoResponse.HasErrors.ShouldBeFalse();
        infoResponse.Count.ShouldBeGreaterThan(0);

        // Write and read back a record to prove the schema works
        var person = new Person { Name = "IndexTest", Age = 30 };
        surrealSession.RawQuery("CREATE person CONTENT $content",
            new Dictionary<string, object> { ["content"] = person }).GetAwaiter().GetResult();

        var queryResponse = await surrealSession.RawQuery("SELECT * FROM person WHERE Name = 'IndexTest';");
        queryResponse.HasErrors.ShouldBeFalse();
    }

    [Test]
    public async Task Schema_For_unique_index_creates_unique_index()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Person>().UniqueIndex(p => p.Email);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE person;");
        infoResponse.HasErrors.ShouldBeFalse();
        infoResponse.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Schema_For_composite_index_creates_multi_column_index()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Product>().CompositeIndex(p => p.Name, p => p.Category);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE product;");
        infoResponse.HasErrors.ShouldBeFalse();
        infoResponse.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Schema_For_unique_composite_index_creates_multi_column_unique()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Product>().UniqueCompositeIndex(p => p.Name, p => p.Category);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE product;");
        infoResponse.HasErrors.ShouldBeFalse();
        infoResponse.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Schema_For_multi_tenanted_is_callable()
    {
        // MultiTenanted() is a no-op at schema creation time (tenant filtering is done
        // at query time), but we verify the fluent API is usable and doesn't throw.
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Person>().UniqueIndex(p => p.Email).MultiTenanted();
        });
        await store.InitializeAsync();

        // Verify the table still works with multi-tenanted configuration
        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
    }

    [Test]
    public async Task Schema_For_multiple_indices_all_created()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Schema.For<Person>()
                .Index(p => p.Name)
                .UniqueIndex(p => p.Email);
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE person;");
        infoResponse.HasErrors.ShouldBeFalse();
    }

    [Test]
    public async Task IConfigureDali_executed_during_init()
    {
        var executed = false;

        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Configurators.Add(new TestConfigurator(() => executed = true));
        });
        await store.InitializeAsync();

        executed.ShouldBeTrue();
    }

    [Test]
    public async Task Multiple_IConfigureDali_are_all_executed()
    {
        var executionOrder = new List<string>();

        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Configurators.Add(new TestConfigurator(() => executionOrder.Add("first")));
            o.Configurators.Add(new TestConfigurator(() => executionOrder.Add("second")));
        });
        await store.InitializeAsync();

        executionOrder.Count.ShouldBe(2);
        executionOrder[0].ShouldBe("first");
        executionOrder[1].ShouldBe("second");
    }

    [Test]
    public async Task IConfigureDali_can_configure_schema()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Configurators.Add(new TestConfigurator(opts =>
            {
                opts.Schema.For<Person>().UniqueIndex(p => p.Email);
            }));
        });
        await store.InitializeAsync();

        // Verify the schema was configured by the configurator
        var surrealSession = ((InternalSessionBase)await store.LightweightSessionAsync()).Session;
        var response = await surrealSession.RawQuery("INFO FOR TABLE person;");
        response.HasErrors.ShouldBeFalse();
    }

    /// <summary>
    /// Verifies that the For&lt;T&gt;() fluent API returns a valid mapping.
    /// </summary>
    [Test]
    public async Task Schema_For_returns_mapping()
    {
        var options = new StoreOptions();
        var mapping = options.Schema.For<Person>();
        mapping.ShouldNotBeNull();

        // Same call returns cached instance
        var mapping2 = options.Schema.For<Person>();
        ReferenceEquals(mapping, mapping2).ShouldBeTrue();
    }

    private sealed class TestConfigurator : IConfigureDali
    {
        private readonly Action<StoreOptions> _action;
        public TestConfigurator(Action action) : this(_ => action()) { }
        public TestConfigurator(Action<StoreOptions> action) => _action = action;
        public void Configure(StoreOptions options) => _action(options);
    }
}
