using TUnit.Core;

namespace Dali.Tests;

public class TenancyTests
{
    [Test]
    public async Task Conjoined_tenants_are_isolated()
    {
        // Create store with Conjoined tenancy
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        // Store doc in tenant-A session
        await using var sessionA = await store.LightweightSessionAsync();
        sessionA.SetTenant("tenant-A");
        sessionA.Store(new TenantPerson { Name = "Alice", Age = 30 });
        await sessionA.SaveChangesAsync();

        // Store doc in tenant-B session
        await using var sessionB = await store.LightweightSessionAsync();
        sessionB.SetTenant("tenant-B");
        sessionB.Store(new TenantPerson { Name = "Bob", Age = 25 });
        await sessionB.SaveChangesAsync();

        // Query tenant-A: should only see tenant-A docs
        await using var queryA = await store.QuerySessionAsync();
        queryA.SetTenant("tenant-A");
        var resultsA = await queryA.Query<TenantPerson>().ToListAsync();
        resultsA.Count.ShouldBe(1);
        resultsA[0].Name.ShouldBe("Alice");
        resultsA[0].TenantId.ShouldBe("tenant-A");

        // Query tenant-B: should only see tenant-B docs
        await using var queryB = await store.QuerySessionAsync();
        queryB.SetTenant("tenant-B");
        var resultsB = await queryB.Query<TenantPerson>().ToListAsync();
        resultsB.Count.ShouldBe(1);
        resultsB[0].Name.ShouldBe("Bob");
        resultsB[0].TenantId.ShouldBe("tenant-B");
    }

    [Test]
    public async Task Conjoined_query_filters_by_tenant()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        // Store 2 docs in tenant-A
        await using var sessionA = await store.LightweightSessionAsync();
        sessionA.SetTenant("tenant-A");
        sessionA.Store(new TenantPerson { Name = "Alice", Age = 30 });
        sessionA.Store(new TenantPerson { Name = "Charlie", Age = 35 });
        await sessionA.SaveChangesAsync();

        // Store 1 doc in tenant-B
        await using var sessionB = await store.LightweightSessionAsync();
        sessionB.SetTenant("tenant-B");
        sessionB.Store(new TenantPerson { Name = "Bob", Age = 25 });
        await sessionB.SaveChangesAsync();

        // Query tenant-A: should return 2
        await using var queryA = await store.QuerySessionAsync();
        queryA.SetTenant("tenant-A");
        var resultsA = await queryA.Query<TenantPerson>().ToListAsync();
        resultsA.Count.ShouldBe(2);

        // Query tenant-B: should return 1
        await using var queryB = await store.QuerySessionAsync();
        queryB.SetTenant("tenant-B");
        var resultsB = await queryB.Query<TenantPerson>().ToListAsync();
        resultsB.Count.ShouldBe(1);

        // Query without tenant: should return all 3
        await using var queryAll = await store.QuerySessionAsync();
        var resultsAll = await queryAll.Query<TenantPerson>().ToListAsync();
        resultsAll.Count.ShouldBe(3);
    }

    [Test]
    public async Task Store_sets_tenant_id_automatically()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        session.SetTenant("my-tenant");

        var person = new TenantPerson { Name = "AutoTenant", Age = 20 };
        session.Store(person);

        // TenantId should be set automatically before save
        person.TenantId.ShouldBe("my-tenant");

        await session.SaveChangesAsync();

        // Verify it persisted
        var all = await session.Query<TenantPerson>().ToListAsync();
        all.Count.ShouldBe(1);
        all[0].TenantId.ShouldBe("my-tenant");
    }

    [Test]
    public async Task Default_tenant_id_from_store_options()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
            o.DefaultTenantId = "default-tenant";
        });
        await store.InitializeAsync();

        // Session should inherit the default tenant ID
        await using var session = await store.LightweightSessionAsync();
        session.TenantId.ShouldBe("default-tenant");

        session.Store(new TenantPerson { Name = "Default", Age = 10 });
        await session.SaveChangesAsync();

        var results = await session.Query<TenantPerson>().ToListAsync();
        results.Count.ShouldBe(1);
        results[0].TenantId.ShouldBe("default-tenant");
    }

    [Test]
    public async Task Non_tenant_models_are_not_filtered()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        session.SetTenant("tenant-X");

        // Person does NOT have a TenantId property, so it should not be filtered
        session.Store(new Person { Name = "NoTenant", Age = 50 });
        await session.SaveChangesAsync();

        // Query Person without tenant filter — should find the record
        var results = await session.Query<Person>().ToListAsync();
        results.Count.ShouldBeGreaterThanOrEqualTo(1);

        // Query with a different tenant session should also find it
        await using var otherSession = await store.QuerySessionAsync();
        otherSession.SetTenant("tenant-Y");
        var otherResults = await otherSession.Query<Person>().ToListAsync();
        otherResults.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Delete_throws_for_wrong_tenant()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        // Store a person in tenant-A
        TenantPerson? savedPerson;
        await using (var sessionA = await store.LightweightSessionAsync())
        {
            sessionA.SetTenant("tenant-A");
            sessionA.Store(new TenantPerson { Name = "Secret", Age = 99 });
            await sessionA.SaveChangesAsync();

            var results = await sessionA.Query<TenantPerson>().ToListAsync();
            savedPerson = results[0];
        }

        // Try to delete from tenant-B — should throw
        await using (var sessionB = await store.LightweightSessionAsync())
        {
            sessionB.SetTenant("tenant-B");
            Should.Throw<InvalidOperationException>(() => sessionB.Delete(savedPerson!));
        }
    }

    [Test]
    public async Task Tenant_clear_removes_filter()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await store.InitializeAsync();

        // Store docs in two tenants
        await using (var sA = await store.LightweightSessionAsync())
        {
            sA.SetTenant("tenant-A");
            sA.Store(new TenantPerson { Name = "FromA" });
            await sA.SaveChangesAsync();
        }

        await using (var sB = await store.LightweightSessionAsync())
        {
            sB.SetTenant("tenant-B");
            sB.Store(new TenantPerson { Name = "FromB" });
            await sB.SaveChangesAsync();
        }

        // Query with tenant filter, then clear it
        await using var query = await store.QuerySessionAsync();
        query.SetTenant("tenant-A");
        var filtered = await query.Query<TenantPerson>().ToListAsync();
        filtered.Count.ShouldBe(1);

        query.ClearTenant();
        var all = await query.Query<TenantPerson>().ToListAsync();
        all.Count.ShouldBe(2);
    }
}
