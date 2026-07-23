using AeroDB.Sable;
using Shouldly;
using SurrealDb.Net.Models;

namespace AeroDB.Tests;

public sealed class TenantMutationSecurityTests
{
    [Test]
    public async Task DeleteWhere_is_parameterized_and_tenant_scoped()
    {
        await using var store = await CreateTenantStoreAsync();
        await StoreTenantPersonAsync(store, "tenant-a", "tenant-a-record", "Alpha", 42);
        await StoreTenantPersonAsync(store, "tenant-b", "tenant-b-record", "Alpha", 42);

        await using (var tenantA = await store.LightweightSessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            await tenantA.DeleteWhere<TenantPerson>(
                person => person.Name.StartsWith("Al") && person.Age == 42);
        }

        await using (var tenantA = await store.QuerySessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            (await tenantA.Query<TenantPerson>().ToListAsync()).ShouldBeEmpty();
        }

        await using (var tenantB = await store.QuerySessionAsync())
        {
            tenantB.SetTenant("tenant-b");
            (await tenantB.Query<TenantPerson>().ToListAsync())
                .ShouldHaveSingleItem()
                .Name.ShouldBe("Alpha");
        }
    }

    [Test]
    public async Task Unsupported_delete_predicate_fails_closed()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        await Should.ThrowAsync<NotSupportedException>(
            async () => await session.DeleteWhere<Person>(
                person => person.Tags.Any(tag => tag.StartsWith("protected"))));
    }

    [Test]
    public async Task HardDelete_by_known_id_cannot_cross_tenants()
    {
        await using var store = await CreateTenantStoreAsync();
        await StoreTenantPersonAsync(
            store, "tenant-b", "tenant-b-hard-delete", "Protected", 7);

        await using (var tenantA = await store.LightweightSessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            tenantA.HardDelete<TenantPerson>("tenant-b-hard-delete");
            await tenantA.SaveChangesAsync();
        }

        await using var tenantB = await store.QuerySessionAsync();
        tenantB.SetTenant("tenant-b");
        (await tenantB.Query<TenantPerson>().ToListAsync())
            .ShouldHaveSingleItem()
            .Name.ShouldBe("Protected");
    }

    [Test]
    public async Task SoftDelete_by_known_id_cannot_cross_tenants()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantSoftDeletePerson>().MultiTenanted();
        });
        await using (var tenantB = await store.LightweightSessionAsync())
        {
            tenantB.SetTenant("tenant-b");
            tenantB.Store(new TenantSoftDeletePerson
            {
                Id = RecordId.From("tenant_soft_delete_person", "tenant-b-soft-delete"),
                Name = "Protected"
            });
            await tenantB.SaveChangesAsync();
        }

        await using (var tenantA = await store.LightweightSessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            await tenantA.SoftDeleteAsync<TenantSoftDeletePerson>(
                "tenant-b-soft-delete");
        }

        await using var tenantBRead = await store.QuerySessionAsync();
        tenantBRead.SetTenant("tenant-b");
        var remaining = (await tenantBRead.Query<TenantSoftDeletePerson>().ToListAsync())
            .ShouldHaveSingleItem();
        remaining.Deleted.ShouldBeFalse();
        remaining.DeletedAt.ShouldBeNull();
    }

    [Test]
    public async Task Multi_tenant_mutation_requires_a_tenant_scoped_session()
    {
        await using var store = await CreateTenantStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await session.HardDeleteWhere<TenantPerson>(
                person => person.Age == 42));
        exception.Message.ShouldContain("tenant-scoped session");
    }

    private static Task<IDocumentStore> CreateTenantStoreAsync() =>
        TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantPerson>().MultiTenanted();
        });

    private static async Task StoreTenantPersonAsync(
        IDocumentStore store,
        string tenantId,
        string recordId,
        string name,
        int age)
    {
        await using var session = await store.LightweightSessionAsync();
        session.SetTenant(tenantId);
        session.Store(new TenantPerson
        {
            Id = RecordId.From("tenant_person", recordId),
            Name = name,
            Age = age
        });
        await session.SaveChangesAsync();
    }

    public sealed class TenantSoftDeletePerson : Record, ISoftDeleted
    {
        public string Name { get; set; } = "";
        public string TenantId { get; set; } = "";
        public bool Deleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
