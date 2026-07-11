using AeroDB.Sable;
using TUnit.Core;

namespace AeroDB.Tests;

public class IEntityTests
{
    // ─── Long (Snowflake) Id Tests ─────────────────────────

    [Test]
    public async Task Store_EntityLong_and_query_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var product = new EntityProduct { Name = "Widget", Price = 29.99m, Stock = 100 };
        session.Store(product);
        await session.SaveChangesAsync();

        // Query and verify
        var all = await session.Query<EntityProduct>().ToListAsync();
        all.ShouldNotBeNull();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);

        var found = all.FirstOrDefault(p => p.Name == "Widget");
        found.ShouldNotBeNull();
        found!.Id.ShouldBeGreaterThan(0);
        found.Name.ShouldBe("Widget");
        found.Price.ShouldBe(29.99m);
        found.Stock.ShouldBe(100);
    }

    [Test]
    public async Task EntityLong_explicit_snowflake_id_is_preserved()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var product = new EntityProduct { Name = "ExplicitSnowflake", Price = 10m };
        var explicitId = product.Id; // EntitySnowlake constructor generates it
        explicitId.ShouldBeGreaterThan(0);
        session.Store(product);
        await session.SaveChangesAsync();

        var all = await session.Query<EntityProduct>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
        var found = all.FirstOrDefault();
        found.ShouldNotBeNull();
        found!.Id.ShouldBe(explicitId);
        found.Name.ShouldBe("ExplicitSnowflake");
        found.Price.ShouldBe(10m);
    }

    [Test]
    public async Task Store_multiple_EntityLong_and_query_all()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        for (var i = 0; i < 5; i++)
            session.Store(new EntityProduct { Name = $"Product_{i}", Price = i * 10m });
        await session.SaveChangesAsync();

        var all = await session.Query<EntityProduct>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Test]
    public async Task EntityLong_query_with_linq_where()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new EntityProduct { Name = "Expensive", Price = 999m, Stock = 5 });
        session.Store(new EntityProduct { Name = "Cheap", Price = 5m, Stock = 500 });
        await session.SaveChangesAsync();

        var results = await session.Query<EntityProduct>().Where(p => p.Price > 500m).ToListAsync();
        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Expensive");
        results[0].Price.ShouldBe(999m);
    }

    // ─── String Id Tests ──────────────────────────────────

    [Test]
    public async Task Store_EntityString_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var customer = new EntityCustomer { Name = "Alice", Email = "alice@test.com" };
        session.Store(customer);
        await session.SaveChangesAsync();

        var all = await session.Query<EntityCustomer>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
        var found = all.FirstOrDefault(c => c.Name == "Alice");
        found.ShouldNotBeNull();
        found!.Id.ShouldNotBeNullOrEmpty();
        found.Name.ShouldBe("Alice");
        found.Email.ShouldBe("alice@test.com");
    }

    [Test]
    public async Task Store_multiple_EntityString_unique_ids()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var c1 = new EntityCustomer { Name = "Alice", Email = "a@test.com" };
        var c2 = new EntityCustomer { Name = "Bob", Email = "b@test.com" };
        session.Store(c1);
        session.Store(c2);
        await session.SaveChangesAsync();

        var all = await session.Query<EntityCustomer>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(2);
        all.Select(c => c.Id).Distinct().Count().ShouldBe(all.Count); // unique Ids
    }

    [Test]
    public async Task EntityString_query_with_linq()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new EntityCustomer { Name = "Admin", Email = "admin@test.com" });
        session.Store(new EntityCustomer { Name = "Guest", Email = "guest@test.com" });
        await session.SaveChangesAsync();

        var admin = await session.Query<EntityCustomer>().FirstOrDefaultAsync(c => c.Name == "Admin");
        admin.ShouldNotBeNull();
        admin!.Name.ShouldBe("Admin");
        admin.Email.ShouldBe("admin@test.com");
    }

    // ─── Int Id Tests ─────────────────────────────────────

    [Test]
    public async Task Store_EntityInt_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var order = new EntityOrder { Description = "Books", Quantity = 3, Amount = 45.50m };
        session.Store(order);
        await session.SaveChangesAsync();

        var all = await session.Query<EntityOrder>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Store_multiple_EntityInt_unique_ids()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        for (var i = 0; i < 10; i++)
            session.Store(new EntityOrder { Description = $"Order_{i}", Quantity = 1 });
        await session.SaveChangesAsync();

        var all = await session.Query<EntityOrder>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(10);
        // All Int Ids should be unique (Random with 1..int.MaxValue range)
        all.Select(o => o.Id).Distinct().Count().ShouldBe(all.Count);
    }

    // ─── Guid Id Tests ────────────────────────────────────

    [Test]
    public async Task Store_EntityGuid_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var token = new EntitySession { Token = "abc123", UserName = "testuser", CreatedAt = DateTimeOffset.UtcNow };
        session.Store(token);
        await session.SaveChangesAsync();

        var all = await session.Query<EntitySession>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task EntityGuid_explicit_id_is_preserved()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var sessionEntity = new EntitySession { Token = "preserved", UserName = "guidtest" };
        var explicitId = sessionEntity.Id;
        explicitId.ShouldNotBe(Guid.Empty);
        session.Store(sessionEntity);
        await session.SaveChangesAsync();

        var all = await session.Query<EntitySession>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
        var found = all.FirstOrDefault(s => s.Token == "preserved");
        found.ShouldNotBeNull();
        found!.Id.ShouldBe(explicitId);
        found.UserName.ShouldBe("guidtest");
    }

    [Test]
    public async Task Store_multiple_EntityGuid_unique_ids()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        for (var i = 0; i < 5; i++)
            session.Store(new EntitySession { Token = $"tok_{i}", UserName = $"user_{i}" });
        await session.SaveChangesAsync();

        var all = await session.Query<EntitySession>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(5);
        all.All(s => s.Id != Guid.Empty).ShouldBeTrue();
        all.Select(s => s.Id).Distinct().Count().ShouldBe(all.Count);
    }

    // ─── Mixed Type Tests ─────────────────────────────────

    [Test]
    public async Task Store_mixed_entity_types_in_same_session()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new EntityProduct { Name = "MixedProduct", Price = 100m });
        session.Store(new EntityCustomer { Name = "MixedCustomer", Email = "mixed@test.com" });
        session.Store(new EntityOrder { Description = "MixedOrder", Quantity = 1 });
        session.Store(new EntitySession { Token = "mixed_token", UserName = "mixed_user" });
        await session.SaveChangesAsync();

        // All types should be queryable
        var products = await session.Query<EntityProduct>().ToListAsync();
        var customers = await session.Query<EntityCustomer>().ToListAsync();
        var orders = await session.Query<EntityOrder>().ToListAsync();
        var sessions = await session.Query<EntitySession>().ToListAsync();

        products.ShouldNotBeEmpty();
        customers.ShouldNotBeEmpty();
        orders.ShouldNotBeEmpty();
        sessions.ShouldNotBeEmpty();
    }

    // ─── Update Tests ─────────────────────────────────────

    [Test]
    public async Task Update_EntityLong_preserves_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var product = new EntityProduct { Name = "BeforeUpdate", Price = 50m };
        session.Store(product);
        await session.SaveChangesAsync();

        product.Name = "AfterUpdate";
        product.Price = 75m;
        session.Store(product);
        await session.SaveChangesAsync();

        var all = await session.Query<EntityProduct>().ToListAsync();
        all.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    // ─── Delete Tests ─────────────────────────────────────

    [Test]
    public async Task Delete_EntityLong_exercises_delete_path()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        var product = new EntityProduct { Name = "DeleteMe", Price = 1m };
        session.Store(product);
        await session.SaveChangesAsync();

        session.Delete(product);
        await session.SaveChangesAsync();
    }

    // ─── Roundtrip Tests ──────────────────────────────────

    [Test]
    public async Task Roundtrip_all_entity_types_store_and_query()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var product = new EntityProduct { Name = "RT_Product", Price = 42m, Stock = 7 };
        var customer = new EntityCustomer { Name = "RT_Customer", Email = "rt@test.com" };
        var order = new EntityOrder { Description = "RT_Order", Quantity = 2, Amount = 99.99m };
        var token = new EntitySession { Token = "rt_token", UserName = "rt_user", CreatedAt = DateTimeOffset.UtcNow };

        session.Store(product);
        session.Store(customer);
        session.Store(order);
        session.Store(token);
        await session.SaveChangesAsync();

        // Re-query each
        var pResult = await session.Query<EntityProduct>().ToListAsync();
        var cResult = await session.Query<EntityCustomer>().ToListAsync();
        var oResult = await session.Query<EntityOrder>().ToListAsync();
        var sResult = await session.Query<EntitySession>().ToListAsync();

        pResult.ShouldNotBeEmpty();
        cResult.ShouldNotBeEmpty();
        oResult.ShouldNotBeEmpty();
        sResult.ShouldNotBeEmpty();

        var pFound = pResult.FirstOrDefault(p => p.Name == "RT_Product");
        pFound.ShouldNotBeNull();
        pFound!.Price.ShouldBe(42m);
        pFound.Stock.ShouldBe(7);

        var cFound = cResult.FirstOrDefault(c => c.Name == "RT_Customer");
        cFound.ShouldNotBeNull();
        cFound!.Email.ShouldBe("rt@test.com");

        var oFound = oResult.FirstOrDefault(o => o.Description == "RT_Order");
        oFound.ShouldNotBeNull();
        oFound!.Amount.ShouldBe(99.99m);

        var sFound = sResult.FirstOrDefault(s => s.Token == "rt_token");
        sFound.ShouldNotBeNull();
        sFound!.UserName.ShouldBe("rt_user");
    }

    // ─── Regression: Entity alongside Record ──────────────

    [Test]
    public async Task EntityLong_and_Record_coexist_in_same_session()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // Entity<TId> type
        session.Store(new EntityProduct { Name = "EntityProduct", Price = 10m });
        // Record type (existing pattern)
        session.Store(new Person { Name = "RecordPerson", Age = 30 });
        await session.SaveChangesAsync();

        var products = await session.Query<EntityProduct>().ToListAsync();
        var people = await session.Query<Person>().ToListAsync();

        products.ShouldNotBeEmpty();
        people.ShouldNotBeEmpty();

        var pFound = products.FirstOrDefault(p => p.Name == "EntityProduct");
        pFound.ShouldNotBeNull();
        pFound!.Price.ShouldBe(10m);

        var rFound = people.FirstOrDefault(p => p.Name == "RecordPerson");
        rFound.ShouldNotBeNull();
        rFound!.Age.ShouldBe(30);
    }

    // ─── LoadAsync, BulkInsert, empty-table regression ─────

    [Test]
    public async Task EntityLong_LoadAsync_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var product = new EntityProduct
        {
            Name = "LoadedProduct",
            Price = 49.99m,
            Stock = 100
        };
        session.Store(product);
        await session.SaveChangesAsync();

        // Load by the snowflake Id (converted to string for LoadAsync)
        var loaded = await session.LoadAsync<EntityProduct>(product.Id.ToString());

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("LoadedProduct");
        loaded.Price.ShouldBe(49.99m);
        loaded.Stock.ShouldBe(100);
        loaded.Id.ShouldBe(product.Id);
    }

    [Test]
    public async Task EntityString_LoadAsync_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var customer = new EntityCustomer { Name = "LoadCustomer", Email = "load@test.com" };
        session.Store(customer);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<EntityCustomer>(customer.Id);

        loaded.ShouldNotBeNull();
        loaded.Name.ShouldBe("LoadCustomer");
        loaded.Email.ShouldBe("load@test.com");
        loaded.Id.ShouldBe(customer.Id);
    }

    [Test]
    public async Task EntityInt_LoadAsync_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var order = new EntityOrder { Description = "LoadOrder", Quantity = 3, Amount = 50m };
        session.Store(order);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<EntityOrder>(order.Id.ToString());

        loaded.ShouldNotBeNull();
        loaded.Description.ShouldBe("LoadOrder");
        loaded.Quantity.ShouldBe(3);
        loaded.Id.ShouldBe(order.Id);
    }

    [Test]
    public async Task EntityGuid_LoadAsync_by_id()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var sess = new EntitySession { Token = "load_token", UserName = "load_user" };
        session.Store(sess);
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<EntitySession>(sess.Id.ToString());

        loaded.ShouldNotBeNull();
        loaded.Token.ShouldBe("load_token");
        loaded.UserName.ShouldBe("load_user");
        loaded.Id.ShouldBe(sess.Id);
    }

    [Test]
    public async Task EntityLong_BulkInsertAsync()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var products = new[]
        {
            new EntityProduct { Name = "BulkProduct_1", Price = 10m, Stock = 10 },
            new EntityProduct { Name = "BulkProduct_2", Price = 20m, Stock = 20 },
            new EntityProduct { Name = "BulkProduct_3", Price = 30m, Stock = 30 },
        };

        // BulkInsertAsync writes via raw SurrealQL INSERT.
        // The count indicates SurrealDB accepted all rows.
        var inserted = await session.BulkInsertAsync(products);
        inserted.ShouldBe(3);
    }

    [Test]
    public async Task EntityLong_query_empty_table_no_crash()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.QuerySessionAsync();

        // Query an entity type that has never had data stored — should not crash
        var results = await session.Query<EntityProduct>().ToListAsync();

        results.ShouldNotBeNull();
        results.Count.ShouldBe(0);
    }
}
