using AeroDB.Sable;
using NSubstitute;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Covers immediate managed writes that must use the active explicit transaction
/// and retain enough mutation history for a later version fence to fail closed.
/// </summary>
public sealed class ImmediateMutationTransactionalSafetyTests
{
    [Test]
    public async Task Queryable_DeleteAsync_rejects_a_read_only_QuerySession_and_preserves_seeded_rows()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "query-session-delete-target", "query-session-delete-target");

        await using (var readOnly = await store.QuerySessionAsync())
        {
            await Should.ThrowAsync<NotSupportedException>(() =>
                readOnly.Query<VersionedPerson>()
                    .Where(person => person.Name == "query-session-delete-target")
                    .DeleteAsync());
        }

        (await LoadVersionedAsync(store, "query-session-delete-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task Queryable_DeleteAsync_on_a_writable_DocumentSession_deletes_matching_rows()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "document-session-delete-target", "document-session-delete-target");

        await using (var writable = await store.LightweightSessionAsync())
        {
            var deleted = await writable.Query<VersionedPerson>()
                .Where(person => person.Name == "document-session-delete-target")
                .DeleteAsync();
            deleted.ShouldBeGreaterThan(0);
        }

        (await LoadVersionedAsync(store, "document-session-delete-target")).ShouldBeNull();
    }

    [Test]
    public async Task Queryable_DeleteAsync_rolls_back_with_the_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "queryable-rollback", "queryable-rollback");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        await session.Query<VersionedPerson>()
            .Where(person => person.Name == "queryable-rollback")
            .DeleteAsync();

        await transaction.RollbackAsync();

        (await LoadVersionedAsync(store, "queryable-rollback")).ShouldNotBeNull();
    }

    [Test]
    public async Task BulkInsertAsync_rolls_back_with_the_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        // Keep the table itself outside the transaction under test. A strict read
        // after rollback must observe an empty table, rather than a missing table.
        await SeedVersionedAsync(store, "bulk-rollback-table-bootstrap", "table-bootstrap");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.BulkInsertAsync(
        [
            new VersionedPerson
            {
                Id = new RecordIdOf<string>("versioned_person", "bulk-rollback"),
                Name = "bulk-rollback",
                Version = 1
            }
        ]);

        await transaction.RollbackAsync();

        (await LoadVersionedAsync(store, "bulk-rollback")).ShouldBeNull();
    }

    [Test]
    public async Task Fence_on_a_disjoint_table_allows_an_immediate_managed_mutation()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "disjoint-immediate-target", "disjoint-immediate-target");
        var softFence = await SeedSoftVersionedAsync(store, "disjoint-fence-target", "disjoint-fence-target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<SoftDeleteVersionedPerson>(softFence, 1);
        await session.SaveChangesAsync();

        await session.Query<VersionedPerson>()
            .Where(person => person.Name == "disjoint-immediate-target")
            .DeleteAsync();

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "disjoint-immediate-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task ClearChanges_does_not_erase_an_immediate_mutation_footprint()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "clear-immediate-fence", "fence");
        await SeedVersionedAsync(store, "clear-immediate-target", "clear-immediate-target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.Query<VersionedPerson>()
            .Where(person => person.Name == "clear-immediate-target")
            .DeleteAsync();

        session.ClearChanges();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "clear-immediate-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task A_failed_explicit_transaction_rejects_a_later_queryable_immediate_mutation()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var staleFence = await SeedVersionedAsync(store, "failed-queryable-fence", "fence");
        await SeedVersionedAsync(store, "failed-queryable-target", "failed-queryable-target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(staleFence, 0);
        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<VersionedPerson>()
                .Where(person => person.Name == "failed-queryable-target")
                .DeleteAsync());

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "failed-queryable-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task Fence_then_queryable_DeleteAsync_is_rejected_and_leaves_the_target_unchanged()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "queryable-fence", "fence");
        await SeedVersionedAsync(store, "queryable-target", "queryable-target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<VersionedPerson>()
                .Where(person => person.Name == "queryable-target")
                .DeleteAsync());

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "queryable-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task Queryable_DeleteAsync_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "queryable-before-fence", "fence");
        await SeedVersionedAsync(store, "queryable-before-target", "queryable-before-target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.Query<VersionedPerson>()
            .Where(person => person.Name == "queryable-before-target")
            .DeleteAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "queryable-before-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task BulkInsertAsync_rejects_a_mapped_database_outside_the_explicit_transaction_scope()
    {
        const string mappedDatabase = "immediate_bulk_mapped";
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>()
                .Schema(mappedDatabase)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.BulkInsertAsync(
            [
                new VersionedPerson
                {
                    Id = new RecordIdOf<string>("versioned_person", "mapped-bulk"),
                    Name = "mapped-bulk",
                    Version = 1
                }
            ]));
        exception.Message.ShouldContain("explicit transaction is scoped to database");
        exception.Message.ShouldContain(mappedDatabase);

        await transaction.RollbackAsync();
        (await LoadVersionedInDatabaseAsync(store, mappedDatabase, "mapped-bulk")).ShouldBeNull();
    }

    [Test]
    public async Task Queryable_DeleteAsync_applies_the_conjoined_tenant_filter()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantPerson>().MultiTenanted();
        });
        await SeedTenantPersonAsync(store, "tenant-a", "queryable-tenant-a", "same-name");
        await SeedTenantPersonAsync(store, "tenant-b", "queryable-tenant-b", "same-name");

        await using (var tenantA = await store.LightweightSessionAsync())
        {
            tenantA.SetTenant("tenant-a");
            await tenantA.Query<TenantPerson>()
                .Where(person => person.Name == "same-name")
                .DeleteAsync();
        }

        await using (var tenantARead = await store.QuerySessionAsync())
        {
            tenantARead.SetTenant("tenant-a");
            (await tenantARead.Query<TenantPerson>().ToListAsync()).ShouldBeEmpty();
        }

        await using (var tenantBRead = await store.QuerySessionAsync())
        {
            tenantBRead.SetTenant("tenant-b");
            (await tenantBRead.Query<TenantPerson>().ToListAsync())
                .ShouldHaveSingleItem()
                .Name.ShouldBe("same-name");
        }
    }

    [Test]
    public async Task Unscoped_queryable_DeleteAsync_rejects_a_conjoined_multi_tenant_type_and_preserves_all_tenants()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantPerson>().MultiTenanted();
        });
        await SeedTenantPersonAsync(store, "tenant-a", "unscoped-queryable-a", "same-name");
        await SeedTenantPersonAsync(store, "tenant-b", "unscoped-queryable-b", "same-name");

        await using (var unscoped = await store.LightweightSessionAsync())
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                unscoped.Query<TenantPerson>()
                    .Where(person => person.Name == "same-name")
                    .DeleteAsync());
        }

        await using (var tenantARead = await store.QuerySessionAsync())
        {
            tenantARead.SetTenant("tenant-a");
            (await tenantARead.Query<TenantPerson>().ToListAsync())
                .ShouldHaveSingleItem()
                .Name.ShouldBe("same-name");
        }

        await using (var tenantBRead = await store.QuerySessionAsync())
        {
            tenantBRead.SetTenant("tenant-b");
            (await tenantBRead.Query<TenantPerson>().ToListAsync())
                .ShouldHaveSingleItem()
                .Name.ShouldBe("same-name");
        }
    }

    [Test]
    public async Task Queryable_DeleteAsync_rejects_a_multi_tenant_mapping_without_a_TenantId_property()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantlessQueryablePerson>().MultiTenanted();
        });
        await using var scoped = await store.LightweightSessionAsync();
        scoped.SetTenant("tenant-a");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            scoped.Query<TenantlessQueryablePerson>()
                .Where(person => person.Name == "must-not-delete")
                .DeleteAsync());
        exception.Message.ShouldContain("TenantId property");
    }

    [Test]
    public async Task Queryable_DeleteAsync_honors_a_pre_canceled_token_without_deleting_documents()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "queryable-canceled", "queryable-canceled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using var session = await store.LightweightSessionAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            session.Query<VersionedPerson>()
                .Where(person => person.Name == "queryable-canceled")
                .DeleteAsync(cancellation.Token));

        (await LoadVersionedAsync(store, "queryable-canceled")).ShouldNotBeNull();
    }

    [Test]
    public async Task Queryable_DeleteAsync_throws_when_the_database_reports_a_statement_error()
    {
        var databaseSession = Substitute.For<ISurrealDbSession>();
        databaseSession.RawQuery(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SurrealDbResponse([new ErrorResult()])));

        await using var session = new DocumentSession(
            Substitute.For<ISurrealDbClient>(),
            databaseSession,
            new StoreOptions(),
            DocumentTracking.None);

        await Should.ThrowAsync<ResponseUnsuccessfulException>(() =>
            session.Query<Person>()
                .Where(person => person.Name == "database-error")
                .DeleteAsync());
    }

    private static async Task<RecordId> SeedVersionedAsync(
        IDocumentStore store,
        string id,
        string name)
    {
        await using var session = await store.LightweightSessionAsync();
        session.Insert(new VersionedPerson
        {
            Id = new RecordIdOf<string>("versioned_person", id),
            Name = name,
            Version = 1
        });
        await session.SaveChangesAsync();
        return RecordId.From("versioned_person", id);
    }

    private static async Task SeedTenantPersonAsync(
        IDocumentStore store,
        string tenantId,
        string id,
        string name)
    {
        await using var session = await store.LightweightSessionAsync();
        session.SetTenant(tenantId);
        session.Insert(new TenantPerson
        {
            Id = RecordId.From("tenant_person", id),
            Name = name,
            TenantId = tenantId
        });
        await session.SaveChangesAsync();
    }

    private static async Task<RecordId> SeedSoftVersionedAsync(
        IDocumentStore store,
        string id,
        string name)
    {
        await using var session = await store.LightweightSessionAsync();
        session.Insert(new SoftDeleteVersionedPerson
        {
            Id = new RecordIdOf<string>("soft_delete_versioned_person", id),
            Name = name,
            Version = 1
        });
        await session.SaveChangesAsync();
        return RecordId.From("soft_delete_versioned_person", id);
    }

    private static async Task<VersionedPerson?> LoadVersionedAsync(
        IDocumentStore store,
        string id)
    {
        await using var session = await store.LightweightSessionAsync();
        return await session.LoadAsync<VersionedPerson>(id);
    }

    private static async Task<VersionedPerson?> LoadVersionedInDatabaseAsync(
        IDocumentStore store,
        string database,
        string id)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var mappedSession = await session.GetSessionForSchemaAsync(database);
        var response = await mappedSession.RawQuery(
            "SELECT * FROM $record;",
            new Dictionary<string, object?>
            {
                ["record"] = RecordId.From("versioned_person", id)
            });
        response.EnsureAllOks();
        return session.DeserializeMappedPocoResponse<VersionedPerson>(response, 0)
            .SingleOrDefault();
    }

    private sealed class ErrorResult : ISurrealDbErrorResult
    {
        public bool IsOk => false;
    }

    public sealed class TenantlessQueryablePerson : Record
    {
        public string Name { get; set; } = string.Empty;
    }
}
