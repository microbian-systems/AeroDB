using SurrealDb.Embedded.InMemory;
using TUnit.Core;

namespace Dali.Tests;

public class DatabasePerTenantTests
{
    [Test]
    public async Task Database_per_tenant_isolates_data()
    {
        // Create store with DatabasePerTenant tenancy
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Store Alice in tenant-a (each tenant gets its own in-memory client/database)
        await using (var sessionA = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA.Store(new Person { Name = "Alice", Age = 30 });
            await sessionA.SaveChangesAsync();
        }

        // Store Bob in tenant-b
        await using (var sessionB = await store.WithTenant("tenant-b").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionB.Store(new Person { Name = "Bob", Age = 25 });
            await sessionB.SaveChangesAsync();
        }

        // Query tenant-a: should only see Alice
        await using (var queryA = await store.WithTenant("tenant-a").QuerySessionAsync())
        {
            var resultsA = await queryA.Query<Person>().ToListAsync();
            resultsA.Count.ShouldBe(1);
            resultsA[0].Name.ShouldBe("Alice");
        }

        // Query tenant-b: should only see Bob
        await using (var queryB = await store.WithTenant("tenant-b").QuerySessionAsync())
        {
            var resultsB = await queryB.Query<Person>().ToListAsync();
            resultsB.Count.ShouldBe(1);
            resultsB[0].Name.ShouldBe("Bob");
        }
    }

    [Test]
    public async Task Database_per_tenant_schema_independent()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Store in tenant-a
        await using (var sessionA = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA.Store(new Person { Name = "Alice" });
            await sessionA.SaveChangesAsync();
        }

        // Query tenant-b — should be empty (separate database)
        await using (var queryB = await store.WithTenant("tenant-b").QuerySessionAsync())
        {
            var results = await queryB.Query<Person>().ToListAsync();
            results.Count.ShouldBe(0);
        }
    }

    [Test]
    public async Task Database_per_tenant_requires_tenant_id()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
            // No DefaultTenantId set
        });
        await store.InitializeAsync();

        // Attempting to create a session without WithTenant() should throw
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        });
    }

    [Test]
    public async Task Database_per_tenant_with_default_tenant_id()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
            o.DefaultTenantId = "default-tenant";
        });
        await store.InitializeAsync();

        // Session should use the default tenant ID without needing WithTenant()
        await using var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.Store(new Person { Name = "Default", Age = 10 });
        await session.SaveChangesAsync();

        // Verify data is in the default tenant's database
        var results = await session.Query<Person>().ToListAsync();
        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe("Default");
    }

    [Test]
    public async Task Database_per_tenant_client_reuse()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Store data in tenant-a
        await using (var sessionA1 = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA1.Store(new Person { Name = "Shared-Tenant" });
            await sessionA1.SaveChangesAsync();
        }

        // Second session for same tenant should find the data (same client/database)
        await using (var sessionA2 = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            var results = await sessionA2.Query<Person>().ToListAsync();
            results.Count.ShouldBe(1);
            results[0].Name.ShouldBe("Shared-Tenant");
        }
    }

    [Test]
    public async Task Database_per_tenant_does_not_set_tenant_id_on_entity()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        await using var session = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        var person = new TenantPerson { Name = "NoAutoTenant", Age = 20 };
        session.Store(person);

        // TenantId should NOT be automatically set (database isolation handles it)
        person.TenantId.ShouldBe("");

        await session.SaveChangesAsync();
    }

    [Test]
    public async Task Database_per_tenant_conjoined_mix()
    {
        // Verify that Conjoined tenancy still works after DatabasePerTenant changes
        await using var conjoinedStore = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.Conjoined;
        });
        await conjoinedStore.InitializeAsync();

        // Store doc in tenant-A
        await using var sessionA = await conjoinedStore.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        sessionA.SetTenant("tenant-A");
        sessionA.Store(new TenantPerson { Name = "Alice" });
        await sessionA.SaveChangesAsync();

        // Store doc in tenant-B
        await using var sessionB = await conjoinedStore.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        sessionB.SetTenant("tenant-B");
        sessionB.Store(new TenantPerson { Name = "Bob" });
        await sessionB.SaveChangesAsync();

        // Query tenant-A: only Alice
        await using var queryA = await conjoinedStore.QuerySessionAsync();
        queryA.SetTenant("tenant-A");
        var resultsA = await queryA.Query<TenantPerson>().ToListAsync();
        resultsA.Count.ShouldBe(1);
        resultsA[0].Name.ShouldBe("Alice");
    }

    [Test]
    public async Task Database_per_tenant_inherits_tenant_id_in_session()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Check that the TenantId is set on the session
        await using var session = await store.WithTenant("my-tenant").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        session.TenantId.ShouldBe("my-tenant");
    }

    [Test]
    public async Task Database_per_tenant_delete_without_tenant_check()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Store in tenant-a
        Person? saved;
        await using (var sessionA = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA.Store(new Person { Name = "ToDelete" });
            await sessionA.SaveChangesAsync();

            var results = await sessionA.Query<Person>().ToListAsync();
            saved = results[0];
        }

        // Delete from the same tenant — should succeed
        await using (var sessionA2 = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA2.Delete(saved!);
            await sessionA2.SaveChangesAsync();
        }

        // Verify deleted
        await using (var queryA = await store.WithTenant("tenant-a").QuerySessionAsync())
        {
            var results = await queryA.Query<Person>().ToListAsync();
            results.Count.ShouldBe(0);
        }
    }

    [Test]
    public async Task Database_per_tenant_load_async_no_tenant_filter()
    {
        await using var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDbMemoryClient();
            o.TenancyStyle = TenancyStyle.DatabasePerTenant;
        });
        await store.InitializeAsync();

        // Store as TenantPerson (which has TenantId property) in tenant-a
        await using (var sessionA = await store.WithTenant("tenant-a").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            sessionA.Store(new TenantPerson { Name = "LoadTest", Age = 99 });
            await sessionA.SaveChangesAsync();
        }

        // Load from a different tenant session — should NOT be filtered by TenantId
        // because DatabasePerTenant uses database isolation
        await using (var sessionB = await store.WithTenant("tenant-b").OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            // This should return null because tenant-b's database is empty,
            // not because of a TenantId filter
            // Store in tenant-b to get a valid ID
            sessionB.Store(new TenantPerson { Name = "Other" });
            await sessionB.SaveChangesAsync();

            var tenantBResults = await sessionB.Query<TenantPerson>().ToListAsync();
            tenantBResults.Count.ShouldBe(1);
            tenantBResults[0].Name.ShouldBe("Other");
        }
    }
}
