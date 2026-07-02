using Microsoft.Extensions.DependencyInjection;
using System.Threading;
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
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
        var infoResponse = await surrealSession.RawQuery("INFO FOR TABLE person;");
        infoResponse.HasErrors.ShouldBeFalse();
        infoResponse.Count.ShouldBeGreaterThan(0);

        // Write and read back a record to prove the schema works
        var person = new Person { Name = "IndexTest", Age = 30 };
        surrealSession.RawQuery("CREATE person CONTENT $content",
            new Dictionary<string, object?> { ["content"] = person }).GetAwaiter().GetResult();

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

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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
            o.Schema.For<Product>().Index(x => new { x.Name, x.Category });
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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
            o.Schema.For<Product>().Index(x => new { x.Name, x.Category }, c => c.IsUnique());
        });
        await store.InitializeAsync();

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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

        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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
        var surrealSession = ((InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None })).Session;
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

    [Test]
    public async Task DI_auto_discovery_resolves_and_applies_configurators()
    {
        var executed = false;
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new TestConfigurator(() => executed = true);
        services.AddSingleton<IConfigureDali>(cfg);
        
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
        });
        
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IDocumentStore>();
        
        executed.ShouldBeTrue();
        // Cleanup
        await store.DisposeAsync();
    }

    [Test]
    public async Task DI_auto_discovery_avoids_double_applying_manual_configurators()
    {
        var callCount = 0;
        var cfg = new TestConfigurator(() => { callCount++; });
        // Register in DI
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfigureDali>(cfg);
        
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
            // ALSO add to manual Configurators list (same instance)
            options.Configurators.Add(cfg);
        });
        
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IDocumentStore>();
        
        callCount.ShouldBe(1); // Should only run once, not twice
        await store.DisposeAsync();
    }

    [Test]
    public async Task Marten_style_modular_config_with_multiple_contributions()
    {
        var configuratorsRun = new List<string>();
        
        var schemaA = new TestConfigurator(opts =>
        {
            opts.Schema.For<Person>().Index(p => p.Name);
            configuratorsRun.Add("SchemaA");
        });
        
        var schemaB = new TestConfigurator(opts =>
        {
            opts.Schema.For<Person>().UniqueIndex(p => p.Email);
            configuratorsRun.Add("SchemaB");
        });
        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfigureDali>(schemaA);
        services.AddSingleton<IConfigureDali>(schemaB);
        
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
        });
        
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IDocumentStore>();
        
        // Both should have been applied (in DI registration order)
        configuratorsRun.Count.ShouldBe(2);
        
        // Verify the indexes exist
        var querySession = (InternalSessionBase)await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var infoResponse = await querySession.Session.RawQuery("INFO FOR TABLE person;");
        infoResponse.HasErrors.ShouldBeFalse();
        
        await store.DisposeAsync();
    }

    [Test]
    public async Task DI_auto_discovery_uses_IServiceProvider_overload()
    {
        var gotServiceProvider = false;
        
        // A configurator that overrides the two-parameter overload
        var cfg = new TwoParamConfigurator(sp => { gotServiceProvider = sp is not null; });
        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfigureDali>(cfg);
        
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
        });
        
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IDocumentStore>();
        
        gotServiceProvider.ShouldBeTrue();
        await store.DisposeAsync();
    }

    [Test]
    public async Task DI_auto_discovery_not_active_with_Documents_For()
    {
        var executed = false;
        var cfg = new TestConfigurator(() => executed = true);
        
        // Register in a separate DI container (not visible to Documents.For)
        var services = new ServiceCollection();
        services.AddSingleton<IConfigureDali>(cfg);
        var sp = services.BuildServiceProvider();
        
        // Documents.For does NOT set ServiceProvider, so configurators from DI are NOT auto-discovered
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Namespace = "test";
            o.Database = "test";
            // Configurator NOT added to Configurators list
        });
        await store.InitializeAsync();
        
        // Should NOT be executed since not in Configurators list and no ServiceProvider
        executed.ShouldBeFalse();
    }

    [Test]
    public async Task DI_auto_discovery_applies_async_configurators()
    {
        var executed = false;
        var cfg = new AsyncTestConfigurator(() => executed = true);
        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAsyncConfigureDali>(cfg);
        
        services.AddDali(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.Namespace = "test";
            options.Database = "test";
        });
        
        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IDocumentStore>();
        
        executed.ShouldBeTrue();
        await store.DisposeAsync();
    }

    private sealed class AsyncTestConfigurator : IAsyncConfigureDali
    {
        private readonly Action _onConfigure;
        public AsyncTestConfigurator(Action onConfigure) => _onConfigure = onConfigure;
        
        public Task ConfigureAsync(StoreOptions options, CancellationToken ct = default)
        {
            _onConfigure();
            return Task.CompletedTask;
        }
    }

    private sealed class TwoParamConfigurator : IConfigureDali
    {
        private readonly Action<IServiceProvider?> _onConfigure;
        public TwoParamConfigurator(Action<IServiceProvider?> onConfigure) => _onConfigure = onConfigure;
        
        // Override the TWO-parameter overload (not the one-param one)
        void IConfigureDali.Configure(IServiceProvider? services, StoreOptions options)
        {
            _onConfigure(services);
        }
        
        // Must also implement the one-param one (required by interface)
        public void Configure(StoreOptions options) { }
    }

    private sealed class TestConfigurator : IConfigureDali
    {
        private readonly Action<StoreOptions> _action;
        public TestConfigurator(Action action) : this(_ => action()) { }
        public TestConfigurator(Action<StoreOptions> action) => _action = action;
        public void Configure(StoreOptions options) => _action(options);
    }
}
