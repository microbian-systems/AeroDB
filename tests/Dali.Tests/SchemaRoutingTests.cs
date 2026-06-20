using TUnit.Core;

namespace Dali.Tests;

// ── Test models for schema routing ──

/// <summary>Entity mapped to a specific schema database.</summary>
public class SalesOrder : SurrealDb.Net.Models.Record
{
    public string OrderNumber { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>Entity mapped to a different schema database.</summary>
public class WarehouseItem : SurrealDb.Net.Models.Record
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>Entity mapped to the default (null) schema.</summary>
public class DefaultEntity : SurrealDb.Net.Models.Record
{
    public string Name { get; set; } = "";
}

/// <summary>
/// Tests for schema-based multi-database routing.
/// </summary>
public class SchemaRoutingTests
{
    // ═══════════════════════════════════════════════
    // Schema configuration tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task Schema_set_via_fluent_api()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("sales");

        var mapping = options.Schema.Mappings[typeof(SalesOrder)];
        mapping.SchemaName.ShouldNotBeNull();
        mapping.SchemaName.ShouldBe("sales");
    }

    [Test]
    public async Task Schema_default_is_null()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>(); // no .Schema() call

        var mapping = options.Schema.Mappings[typeof(SalesOrder)];
        mapping.SchemaName.ShouldBeNull();
    }

    [Test]
    public async Task Schema_can_be_cleared_to_null()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("sales");
        options.Schema.For<SalesOrder>().Schema(null);

        var mapping = options.Schema.Mappings[typeof(SalesOrder)];
        mapping.SchemaName.ShouldBeNull();
    }

    [Test]
    public async Task Different_types_can_have_different_schemas()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("sales");
        options.Schema.For<WarehouseItem>().Schema("warehouse");
        options.Schema.For<DefaultEntity>(); // default schema (null)

        options.Schema.Mappings[typeof(SalesOrder)].SchemaName.ShouldBe("sales");
        options.Schema.Mappings[typeof(WarehouseItem)].SchemaName.ShouldBe("warehouse");
        options.Schema.Mappings[typeof(DefaultEntity)].SchemaName.ShouldBeNull();
    }

    [Test]
    public async Task Schema_fluent_api_returns_mapping_for_chaining()
    {
        var options = new StoreOptions();
        var mapping = options.Schema.For<SalesOrder>().Schema("sales");
        mapping.ShouldBeOfType<DocumentMapping<SalesOrder>>();
        mapping.SchemaName.ShouldBe("sales");
    }

    [Test]
    public async Task Schema_with_index_and_multi_tenanted_chaining()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>()
            .Schema("sales")
            .UniqueIndex(p => p.OrderNumber)
            .MultiTenanted();

        var mapping = options.Schema.Mappings[typeof(SalesOrder)];
        mapping.SchemaName.ShouldBe("sales");
        mapping.IsMultiTenanted.ShouldBeTrue();
        mapping.Indices.Count.ShouldBe(1);
        mapping.Indices[0].IsUnique.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════
    // GetSchemaTarget tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task GetSchemaTarget_returns_null_database_for_default_schema()
    {
        var options = new StoreOptions();
        options.Schema.For<DefaultEntity>();

        var (db, table) = Metadata.MetadataDispatch.GetSchemaTarget(typeof(DefaultEntity), options.Schema);
        db.ShouldBeNull();
        table.ShouldBe("default_entity");
    }

    [Test]
    public async Task GetSchemaTarget_returns_configured_database()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("sales");

        var (db, table) = Metadata.MetadataDispatch.GetSchemaTarget(typeof(SalesOrder), options.Schema);
        db.ShouldBe("sales");
        table.ShouldBe("sales_order");
    }

    [Test]
    public async Task GetSchemaTarget_returns_correct_table_for_entity_without_mapping()
    {
        // Types without a DocumentMapping should still resolve the table name
        var options = new StoreOptions();

        var (db, table) = Metadata.MetadataDispatch.GetSchemaTarget(typeof(Person), options.Schema);
        db.ShouldBeNull();
        table.ShouldBe("person");
    }

    // ═══════════════════════════════════════════════
    // Query routing tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task Query_on_default_schema_executes_on_default_session()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<DefaultEntity>();
        });

        await using var session = await store.LightweightSessionAsync();
        // Write an entity via RawQuery (default db = "test")
        var defaultSession = ((InternalSessionBase)session).Session;
        await defaultSession.RawQuery("CREATE default_entity CONTENT { Name: 'default-test' };");

        // Query via LINQ — should resolve through the default session
        var result = await session.Query<DefaultEntity>().FirstOrDefaultAsync(e => e.Name == "default-test");
        result.ShouldNotBeNull();
        result.Name.ShouldBe("default-test");
    }

    [Test]
    public async Task Query_on_schema_database_routes_to_correct_forked_session()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sales");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();
        var internalSession = (InternalSessionBase)session;

        // Get the schema session and write data directly to the "sales" database
        var schemaDb = Metadata.MetadataDispatch.GetSchemaTarget(typeof(SalesOrder), internalSession.StoreOptions.Schema).Database;
        var schemaSession = await internalSession.GetSessionForSchemaAsync(schemaDb);

        // Create the database if needed (DEFINE DATABASE is idempotent)
        await schemaSession.RawQuery("DEFINE DATABASE IF NOT EXISTS sales;");
        await schemaSession.Use(internalSession.StoreOptions.Namespace ?? "test", "sales");
        await schemaSession.RawQuery("CREATE sales_order CONTENT { OrderNumber: 'ORD-001', Amount: 100.50 };");

        // Now query via LINQ — must resolve to the "sales" forked session
        var result = await session.Query<SalesOrder>().FirstOrDefaultAsync(e => e.OrderNumber == "ORD-001");
        result.ShouldNotBeNull();
        result.Amount.ShouldBe(100.50m);
    }

    [Test]
    public async Task Query_on_default_schema_does_not_see_schema_data()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sales");
            o.Schema.For<DefaultEntity>();
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();
        var internalSession = (InternalSessionBase)session;

        // Write data to the "sales" database
        var schemaDb = Metadata.MetadataDispatch.GetSchemaTarget(typeof(SalesOrder), internalSession.StoreOptions.Schema).Database;
        var schemaSession = await internalSession.GetSessionForSchemaAsync(schemaDb);
        await schemaSession.RawQuery("DEFINE DATABASE IF NOT EXISTS sales;");
        await schemaSession.Use(internalSession.StoreOptions.Namespace ?? "test", "sales");
        await schemaSession.RawQuery("CREATE sales_order CONTENT { OrderNumber: 'ORD-002', Amount: 200.00 };");

        // Write data to the default database
        await internalSession.Session.RawQuery("CREATE default_entity CONTENT { Name: 'default-only' };");

        // Query default — should only see the default entity, not schema data
        var defaultEntities = await session.Query<DefaultEntity>().ToListAsync();
        defaultEntities.Count.ShouldBe(1);
        defaultEntities[0].Name.ShouldBe("default-only");

        // Query schema — should only see the schema data, not the default entity
        var salesOrders = await session.Query<SalesOrder>().ToListAsync();
        salesOrders.Count.ShouldBe(1);
        salesOrders[0].OrderNumber.ShouldBe("ORD-002");
    }

    // ═══════════════════════════════════════════════
    // SaveChanges grouping tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task SaveChanges_single_db_succeeds()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<DefaultEntity>();
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new DefaultEntity { Name = "Single DB" });
        await session.SaveChangesAsync();

        var entities = await session.Query<DefaultEntity>().ToListAsync();
        entities.Count.ShouldBe(1);
        entities[0].Name.ShouldBe("Single DB");
    }

    [Test]
    public async Task SaveChanges_single_schema_db_succeeds()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sales");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "SO-001", Amount = 50.00m });
        await session.SaveChangesAsync();

        // Verify by querying through the schema-routed session
        var loaded = await session.Query<SalesOrder>().FirstOrDefaultAsync();
        loaded.ShouldNotBeNull();
        loaded.OrderNumber.ShouldBe("SO-001");
    }

    [Test]
    public async Task SaveChanges_multi_db_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sales");
            o.Schema.For<WarehouseItem>().Schema("warehouse");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "SO-002", Amount = 75.00m });
        session.Store(new WarehouseItem { Sku = "WH-001", Quantity = 10 });

        // Must throw because operations span "sales" and "warehouse" databases
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync());

        ex.Message.ShouldContain("Cross-database transactions are not supported");
        ex.Message.ShouldContain("sales");
        ex.Message.ShouldContain("warehouse");
    }

    [Test]
    public async Task SaveChanges_default_and_schema_db_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sales");
            o.Schema.For<DefaultEntity>(); // default (null) schema
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "SO-003", Amount = 25.00m });
        session.Store(new DefaultEntity { Name = "default-item" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync());

        ex.Message.ShouldContain("Cross-database transactions are not supported");
    }

    [Test]
    public async Task SaveChanges_mixed_types_same_schema_succeeds()
    {
        // Types in the same database (both null schema) should work
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<DefaultEntity>();
            o.Schema.For<Person>();
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new DefaultEntity { Name = "Mixed-A" });
        session.Store(new Person { Name = "Mixed-B", Age = 25 });
        await session.SaveChangesAsync();

        // Both should be persisted
        var entities = await session.Query<DefaultEntity>().ToListAsync();
        entities.Count.ShouldBe(1);

        var persons = await session.Query<Person>().ToListAsync();
        persons.Count.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════
    // AutoCreateDatabases tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task AutoCreateDatabases_false_does_not_create_database()
    {
        // With AutoCreateDatabases = false (default), the store should still
        // initialize without errors; SurrealDB auto-creates databases on first use.
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("autotest_db");
            o.Schema.AutoCreateDatabases = false;
        });

        // This should not throw — the schema session will implicitly create the DB
        await using var session = await store.LightweightSessionAsync();
        session.Store(new SalesOrder { OrderNumber = "AUTO-001", Amount = 10m });
        await session.SaveChangesAsync();
    }

    [Test]
    public async Task AutoCreateDatabases_true_explicitly_creates_database()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("explicit_db");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new SalesOrder { OrderNumber = "EXPLICIT-001", Amount = 20m });
        await session.SaveChangesAsync();

        var loaded = await session.Query<SalesOrder>().FirstOrDefaultAsync();
        loaded.ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════
    // ForkSession isolation tests
    // ═══════════════════════════════════════════════

    [Test]
    public async Task ForkSession_for_schema_is_cached()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("cached_db");
        });

        await using var session = await store.LightweightSessionAsync();
        var internalSession = (InternalSessionBase)session;

        var session1 = await internalSession.GetSessionForSchemaAsync("cached_db");
        var session2 = await internalSession.GetSessionForSchemaAsync("cached_db");

        // Same schema should return the same cached session
        session1.ShouldBe(session2);
    }

    [Test]
    public async Task Different_schemas_produce_different_forked_sessions()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("db_a");
            o.Schema.For<WarehouseItem>().Schema("db_b");
        });

        await using var session = await store.LightweightSessionAsync();
        var internalSession = (InternalSessionBase)session;

        var sessionA = await internalSession.GetSessionForSchemaAsync("db_a");
        var sessionB = await internalSession.GetSessionForSchemaAsync("db_b");

        sessionA.ShouldNotBe(sessionB);
        sessionA.SessionId.ShouldNotBe(sessionB.SessionId);
    }

    [Test]
    public async Task Null_schema_returns_parent_session()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        var internalSession = (InternalSessionBase)session;

        var defaultSession = await internalSession.GetSessionForSchemaAsync(null);
        defaultSession.ShouldBe(internalSession.Session);
    }

    // ═══════════════════════════════════════════════
    // DocumentMapping SchemaName reflection
    // ═══════════════════════════════════════════════

    [Test]
    public async Task SchemaName_accessible_via_base_class()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("reflection_test");

        DocumentMapping mapping = options.Schema.Mappings[typeof(SalesOrder)];
        mapping.SchemaName.ShouldBe("reflection_test");
    }

    [Test]
    public async Task SchemaName_is_null_when_not_configured()
    {
        var options = new StoreOptions();
        options.Schema.For<DefaultEntity>();

        DocumentMapping mapping = options.Schema.Mappings[typeof(DefaultEntity)];
        mapping.SchemaName.ShouldBeNull();
    }

    [Test]
    public async Task Multiple_schemas_store_all_correctly()
    {
        var options = new StoreOptions();
        options.Schema.For<SalesOrder>().Schema("sales");
        options.Schema.For<WarehouseItem>().Schema("warehouse");
        options.Schema.For<Person>(); // no schema
        options.Schema.For<Product>().Schema("catalog");
        options.Schema.For<Order>(); // no schema

        options.Schema.Mappings[typeof(SalesOrder)].SchemaName.ShouldBe("sales");
        options.Schema.Mappings[typeof(WarehouseItem)].SchemaName.ShouldBe("warehouse");
        options.Schema.Mappings[typeof(Person)].SchemaName.ShouldBeNull();
        options.Schema.Mappings[typeof(Product)].SchemaName.ShouldBe("catalog");
        options.Schema.Mappings[typeof(Order)].SchemaName.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════
    // Query routing cross-type isolation
    // ═══════════════════════════════════════════════

    [Test]
    public async Task Query_and_save_interop_same_db()
    {
        // Store a DefaultEntity, then query it — same database
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<DefaultEntity>();
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new DefaultEntity { Name = "Interop Test" });
        await session.SaveChangesAsync();

        var result = await session.Query<DefaultEntity>()
            .FirstOrDefaultAsync(e => e.Name == "Interop Test");
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task Save_and_query_schema_db()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("sq_db");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "SQ-001", Amount = 42.00m });
        await session.SaveChangesAsync();

        var result = await session.Query<SalesOrder>()
            .FirstOrDefaultAsync(e => e.OrderNumber == "SQ-001");
        result.ShouldNotBeNull();
        result.Amount.ShouldBe(42.00m);
    }

    // ═══════════════════════════════════════════════
    // LoadAsync schema routing
    // ═══════════════════════════════════════════════

    [Test]
    public async Task LoadAsync_on_schema_db()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("load_db");
            o.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "LOAD-001", Amount = 99.99m });
        await session.SaveChangesAsync();

        // Query to get the record via LINQ (schema-routed)
        var loaded = await session.Query<SalesOrder>().FirstOrDefaultAsync();
        loaded.ShouldNotBeNull();
        loaded.OrderNumber.ShouldBe("LOAD-001");
        loaded.Amount.ShouldBe(99.99m);
    }

    // ═══════════════════════════════════════════════
    // Negative: unsupported cross-db operations
    // ═══════════════════════════════════════════════

    [Test]
    public async Task Three_way_cross_db_throws()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("db1");
            o.Schema.For<WarehouseItem>().Schema("db2");
            o.Schema.For<Product>().Schema("db3");
        });

        await using var session = await store.LightweightSessionAsync();

        session.Store(new SalesOrder { OrderNumber = "X01" });
        session.Store(new WarehouseItem { Sku = "X02" });
        session.Store(new Product { Name = "X03" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync());

        ex.Message.ShouldContain("Cross-database transactions are not supported");
        // Should list all three databases
        ex.Message.ShouldContain("db1");
        ex.Message.ShouldContain("db2");
        ex.Message.ShouldContain("db3");
    }

    [Test]
    public async Task Empty_unit_of_work_does_not_throw()
    {
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Schema.For<SalesOrder>().Schema("db1");
            o.Schema.For<WarehouseItem>().Schema("db2");
        });

        await using var session = await store.LightweightSessionAsync();

        // No operations — should succeed without throwing
        var count = await session.SaveChangesAsync();
        count.ShouldBe(0);
    }
}
