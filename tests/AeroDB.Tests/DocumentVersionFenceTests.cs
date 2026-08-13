using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class DocumentVersionFenceTests
{
    [Test]
    public async Task Guard_only_save_commits_version_and_reports_change_set()
    {
        var listener = new CapturingListener();
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Listeners.Add(listener);
            options.Schema.For<VersionedPerson>().SetSchemaMode(SchemaMode.Flexible);
        });
        await SeedVersionAsync<VersionedPerson>(store, "fence-success", 4);

        await using var session = await store.LightweightSessionAsync();
        var recordId = RecordId.From("versioned_person", "fence-success");
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 4);

        fence.Status.ShouldBe(VersionFenceStatus.Queued);
        fence.CommittedVersion.ShouldBeNull();

        (await session.SaveChangesAsync()).ShouldBe(1);

        fence.Status.ShouldBe(VersionFenceStatus.Committed);
        fence.CommittedVersion.ShouldBe(5);
        (await ReadVersionAsync(store, recordId)).ShouldBe(5);
        listener.LastChangeSet.ShouldNotBeNull();
        listener.LastChangeSet.HasChanges.ShouldBeTrue();
        listener.LastChangeSet.VersionFences.ShouldHaveSingleItem().ShouldBeSameAs(fence);
    }

    [Test]
    public async Task Long_identity_overload_commits_version_fence()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<LongVersionedDocument>().SetSchemaMode(SchemaMode.Flexible));
        var recordId = RecordId.From("long_versioned_document", 42L);
        await SeedVersionAsync<LongVersionedDocument>(store, recordId, 8);

        await using var session = await store.LightweightSessionAsync();
        var fence = session.FenceExpectedVersion<LongVersionedDocument>(42L, 8);

        (await session.SaveChangesAsync()).ShouldBe(1);
        fence.CommittedVersion.ShouldBe(9);
        (await ReadVersionAsync(store, recordId)).ShouldBe(9);
    }

    [Test]
    public async Task Stale_fence_fails_closed_and_does_not_change_version()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-stale");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 3);

        await using var session = await store.LightweightSessionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>("fence-stale", 2);

        var exception = await Should.ThrowAsync<ConcurrencyException>(
            () => session.SaveChangesAsync());

        exception.ExpectedVersion.ShouldBe(2);
        exception.ActualVersion.ShouldBe(3);
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        fence.CommittedVersion.ShouldBeNull();
        (await ReadVersionAsync(store, recordId)).ShouldBe(3);
    }

    [Test]
    public async Task Database_response_error_fails_closed_instead_of_skipping_fence()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-response-error");
        await using (var seedSession = (DocumentSession)await store.LightweightSessionAsync())
        {
            var response = await seedSession.Session.RawQuery(
                "CREATE $record CONTENT { name: 'seed', version: 'invalid' };",
                new Dictionary<string, object?> { ["record"] = recordId });
            response.EnsureAllOks();
        }

        await using var session = await store.LightweightSessionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 1);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());

        exception.InnerException.ShouldNotBeNull();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        fence.CommittedVersion.ShouldBeNull();
        (await ReadStringVersionAsync(store, recordId)).ShouldBe("invalid");
    }

    [Test]
    public async Task Multiple_fences_roll_back_when_any_fence_is_stale()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var firstId = RecordId.From("versioned_person", "fence-multi-first");
        var secondId = RecordId.From("versioned_person", "fence-multi-second");
        await SeedVersionAsync<VersionedPerson>(store, firstId, 1);
        await SeedVersionAsync<VersionedPerson>(store, secondId, 2);

        await using var session = await store.LightweightSessionAsync();
        var first = session.FenceExpectedVersion<VersionedPerson>("fence-multi-first", 1);
        var stale = session.FenceExpectedVersion<VersionedPerson>("fence-multi-second", 1);

        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync());

        first.Status.ShouldBe(VersionFenceStatus.RolledBack);
        stale.Status.ShouldBe(VersionFenceStatus.RolledBack);
        first.CommittedVersion.ShouldBeNull();
        stale.CommittedVersion.ShouldBeNull();
        (await ReadVersionAsync(store, firstId)).ShouldBe(1);
        (await ReadVersionAsync(store, secondId)).ShouldBe(2);
    }

    [Test]
    public async Task Explicit_transaction_exposes_version_only_after_commit()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-explicit-commit");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 6);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 6);

        (await session.SaveChangesAsync()).ShouldBe(1);
        fence.Status.ShouldBe(VersionFenceStatus.Applied);
        fence.CommittedVersion.ShouldBeNull();

        await transaction.CommitAsync();

        fence.Status.ShouldBe(VersionFenceStatus.Committed);
        fence.CommittedVersion.ShouldBe(7);
        (await ReadVersionAsync(store, recordId)).ShouldBe(7);
    }

    [Test]
    public async Task Committed_fence_synchronizes_identity_map_instance()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-tracked-auto");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 4);

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });
        var tracked = await session.LoadAsync<VersionedPerson>("fence-tracked-auto");
        tracked.ShouldNotBeNull();
        tracked.Version.ShouldBe(4);

        session.FenceExpectedVersion<VersionedPerson>(recordId, 4);
        await session.SaveChangesAsync();

        tracked.Version.ShouldBe(5);
        session.VersionFor(tracked).ShouldBe(5);
        var reloaded = await session.LoadAsync<VersionedPerson>("fence-tracked-auto");
        reloaded.ShouldNotBeNull();
        reloaded.ShouldBeSameAs(tracked);
        reloaded.Version.ShouldBe(5);
    }

    [Test]
    public async Task Explicit_fence_synchronizes_tracked_instance_only_after_commit()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-tracked-explicit");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 8);

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });
        var tracked = await session.LoadAsync<VersionedPerson>("fence-tracked-explicit");
        tracked.ShouldNotBeNull();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(recordId, 8);

        await session.SaveChangesAsync();

        tracked.Version.ShouldBe(8);
        session.VersionFor(tracked).ShouldBe(8);

        await transaction.CommitAsync();

        tracked.Version.ShouldBe(9);
        session.VersionFor(tracked).ShouldBe(9);
    }

    [Test]
    public async Task Explicit_transaction_rollback_marks_fence_and_discards_increment()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-explicit-rollback");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 10);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 10);

        await session.SaveChangesAsync();
        fence.Status.ShouldBe(VersionFenceStatus.Applied);

        await transaction.RollbackAsync();

        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        fence.CommittedVersion.ShouldBeNull();
        (await ReadVersionAsync(store, recordId)).ShouldBe(10);
    }

    [Test]
    public async Task Failed_explicit_fence_dooms_transaction_until_rollback()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-explicit-failed");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 12);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 11);

        await Should.ThrowAsync<ConcurrencyException>(() => session.SaveChangesAsync());
        fence.Status.ShouldBe(VersionFenceStatus.Failed);
        fence.CommittedVersion.ShouldBeNull();

        var commitError = await Should.ThrowAsync<InvalidOperationException>(
            () => transaction.CommitAsync());
        commitError.Message.ShouldContain("cannot be committed");

        await transaction.RollbackAsync();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        (await ReadVersionAsync(store, recordId)).ShouldBe(12);
    }

    [Test]
    public async Task Duplicate_fence_is_deduplicated_and_conflicting_expectation_is_rejected()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var first = session.FenceExpectedVersion<VersionedPerson>("fence-dedup", 1);
        var duplicate = session.FenceExpectedVersion<VersionedPerson>("fence-dedup", 1);

        duplicate.ShouldBeSameAs(first);
        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("fence-dedup", 2));

        session.ClearChanges();
        first.Status.ShouldBe(VersionFenceStatus.RolledBack);
    }

    [Test]
    public async Task Fence_rejects_overlapping_document_write()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var recordId = RecordId.From("versioned_person", "fence-overlap");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 1);

        await using var session = await store.LightweightSessionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 1);
        var document = await session.LoadAsync<VersionedPerson>("fence-overlap");
        document.ShouldNotBeNull();
        document.Name = "overlapping update";
        session.Store(document);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());
        exception.Message.ShouldContain("both version-fenced and queued");
        fence.Status.ShouldBe(VersionFenceStatus.Queued);

        session.ClearChanges();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
    }

    [Test]
    public async Task Fence_rejects_opaque_queued_mutation()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>("opaque-overlap", 1);
        session.QueueSqlCommand(
            "{database}",
            "UPDATE versioned_person SET name = 'unsafe';");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());

        exception.Message.ShouldContain("record overlap cannot be proven safe");
        fence.Status.ShouldBe(VersionFenceStatus.Queued);
        session.ClearChanges();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
    }

    [Test]
    public async Task Fence_rejects_record_table_mismatch()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        var exception = Should.Throw<ArgumentException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(
                RecordId.From("person", "wrong-table"),
                1));

        exception.Message.ShouldContain("does not match mapped table");
    }

    [Test]
    public async Task Fence_requires_versioned_type_and_valid_expected_version()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<Person>("not-versioned", 1));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("negative-version", -1));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            session.FenceExpectedVersion<VersionedPerson>("overflow-version", long.MaxValue));
    }

    [Test]
    public async Task Fence_rejects_tenant_scope_change_before_save()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<TenantVersionedDocument>()
                .MultiTenanted()
                .SetSchemaMode(SchemaMode.Flexible);
        });
        var recordId = RecordId.From("tenant_versioned_document", "tenant-scope");
        await SeedVersionAsync<TenantVersionedDocument>(
            store,
            recordId,
            1,
            "tenant-a");

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { TenantId = "tenant-a" });
        var fence = session.FenceExpectedVersion<TenantVersionedDocument>(recordId, 1);
        session.ForTenant("tenant-b");

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());
        exception.Message.ShouldContain("session tenant changed");
        fence.Status.ShouldBe(VersionFenceStatus.Queued);

        session.ClearChanges();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
    }

    [Test]
    public async Task Explicit_transaction_rejects_fence_mapped_to_another_database_without_touching_same_id()
    {
        const string mappedDatabase = "fence_explicit_mapped";
        const string id = "same-id-fence";
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>()
                .Schema(mappedDatabase)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });
        var recordId = RecordId.From("versioned_person", id);
        await SeedVersionInDatabaseAsync<VersionedPerson>(store, null, recordId, 40, "default");
        await SeedVersionInDatabaseAsync<VersionedPerson>(store, mappedDatabase, recordId, 7, "mapped");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var fence = session.FenceExpectedVersion<VersionedPerson>(recordId, 7);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());

        exception.Message.ShouldContain("explicit transaction is scoped to database");
        exception.Message.ShouldContain(mappedDatabase);
        fence.Status.ShouldBe(VersionFenceStatus.Queued);

        await transaction.RollbackAsync();
        fence.Status.ShouldBe(VersionFenceStatus.RolledBack);
        (await ReadVersionInDatabaseAsync(store, null, recordId)).ShouldBe(40);
        (await ReadVersionInDatabaseAsync(store, mappedDatabase, recordId)).ShouldBe(7);
    }

    [Test]
    public async Task Explicit_transaction_rejects_write_mapped_to_another_database_without_touching_same_id()
    {
        const string mappedDatabase = "write_explicit_mapped";
        const string id = "same-id-write";
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>()
                .Schema(mappedDatabase)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });
        var recordId = RecordId.From("versioned_person", id);
        await SeedVersionInDatabaseAsync<VersionedPerson>(store, null, recordId, 20, "default");
        await SeedVersionInDatabaseAsync<VersionedPerson>(store, mappedDatabase, recordId, 5, "mapped");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.Update(new VersionedPerson
        {
            Id = new RecordIdOf<string>("versioned_person", id),
            Name = "must-not-be-written",
            Version = 5
        });

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());

        exception.Message.ShouldContain("explicit transaction is scoped to database");
        exception.Message.ShouldContain(mappedDatabase);

        await transaction.RollbackAsync();
        (await ReadNameInDatabaseAsync(store, null, recordId)).ShouldBe("default");
        (await ReadNameInDatabaseAsync(store, mappedDatabase, recordId)).ShouldBe("mapped");
    }

    [Test]
    public async Task Two_sessions_racing_the_same_fence_allow_exactly_one_commit()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
            options.Schema.For<VersionedPerson>().SetSchemaMode(SchemaMode.Flexible));
        var recordId = RecordId.From("versioned_person", "two-session-race");
        await SeedVersionAsync<VersionedPerson>(store, recordId, 1);

        await using var firstSession = await store.LightweightSessionAsync();
        await using var secondSession = await store.LightweightSessionAsync();
        var firstFence = firstSession.FenceExpectedVersion<VersionedPerson>(recordId, 1);
        var secondFence = secondSession.FenceExpectedVersion<VersionedPerson>(recordId, 1);

        var startGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = CaptureFailureAsync(async () =>
        {
            await startGate.Task;
            await firstSession.SaveChangesAsync();
        });
        var secondAttempt = CaptureFailureAsync(async () =>
        {
            await startGate.Task;
            await secondSession.SaveChangesAsync();
        });
        startGate.SetResult(true);

        var outcomes = await Task.WhenAll(firstAttempt, secondAttempt);

        outcomes.Count(exception => exception is null).ShouldBe(1);
        var failures = outcomes.Where(exception => exception is not null).ToArray();
        failures.Length.ShouldBe(1);
        var failure = failures[0]!;
        (failure is ConcurrencyException or InvalidOperationException).ShouldBeTrue();
        if (failure is InvalidOperationException wrapped)
        {
            wrapped.InnerException.ShouldNotBeNull();
        }
        new[] { firstFence, secondFence }
            .Count(fence => fence.Status == VersionFenceStatus.Committed)
            .ShouldBe(1);
        (await ReadVersionAsync(store, recordId)).ShouldBe(2);
    }

    [Test]
    public async Task Fences_participate_in_cross_database_validation()
    {
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>().Schema("fence_db_one");
            options.Schema.For<LongVersionedDocument>().Schema("fence_db_two");
        });
        await using var session = await store.LightweightSessionAsync();
        var first = session.FenceExpectedVersion<VersionedPerson>("cross-db", 1);
        var second = session.FenceExpectedVersion<LongVersionedDocument>(42L, 1);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync());

        exception.Message.ShouldContain("Cross-database transactions are not supported");
        exception.Message.ShouldContain("fence_db_one");
        exception.Message.ShouldContain("fence_db_two");
        first.Status.ShouldBe(VersionFenceStatus.Queued);
        second.Status.ShouldBe(VersionFenceStatus.Queued);

        session.ClearChanges();
        first.Status.ShouldBe(VersionFenceStatus.RolledBack);
        second.Status.ShouldBe(VersionFenceStatus.RolledBack);
    }

    private static async Task SeedVersionAsync<T>(
        IDocumentStore store,
        string id,
        long version,
        string? tenantId = null)
        where T : class
        => await SeedVersionAsync<T>(
            store,
            RecordId.From(ToSnakeCase(typeof(T).Name), id),
            version,
            tenantId);

    private static async Task SeedVersionAsync<T>(
        IDocumentStore store,
        RecordId recordId,
        long version,
        string? tenantId = null)
        where T : class
        => await SeedVersionInDatabaseAsync<T>(
            store,
            null,
            recordId,
            version,
            "seed",
            tenantId);

    private static async Task SeedVersionInDatabaseAsync<T>(
        IDocumentStore store,
        string? database,
        RecordId recordId,
        long version,
        string name,
        string? tenantId = null)
        where T : class
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var targetSession = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var parameters = new Dictionary<string, object?>
        {
            ["record"] = recordId,
            ["version"] = version,
            ["tenant"] = tenantId,
            ["name"] = name
        };
        var response = await targetSession.RawQuery(
            "CREATE $record CONTENT { name: $name, version: $version, tenant_id: $tenant };",
            parameters);
        response.EnsureAllOks();
    }

    private static async Task<long> ReadVersionAsync(
        IDocumentStore store,
        RecordId recordId)
        => await ReadVersionInDatabaseAsync(store, null, recordId);

    private static async Task<long> ReadVersionInDatabaseAsync(
        IDocumentStore store,
        string? database,
        RecordId recordId)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var targetSession = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await targetSession.RawQuery(
            "SELECT VALUE version FROM $record;",
            new Dictionary<string, object?> { ["record"] = recordId });
        response.EnsureAllOks();
        return (response.GetValue<List<long>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task<string> ReadNameInDatabaseAsync(
        IDocumentStore store,
        string? database,
        RecordId recordId)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var targetSession = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await targetSession.RawQuery(
            "SELECT VALUE name FROM $record;",
            new Dictionary<string, object?> { ["record"] = recordId });
        response.EnsureAllOks();
        return (response.GetValue<List<string>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task<string> ReadStringVersionAsync(
        IDocumentStore store,
        RecordId recordId)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            "SELECT VALUE version FROM $record;",
            new Dictionary<string, object?> { ["record"] = recordId });
        response.EnsureAllOks();
        return (response.GetValue<List<string>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static string ToSnakeCase(string name)
        => string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class CapturingListener : IDocumentSessionListener
    {
        public IChangeSet? LastChangeSet { get; private set; }

        public Task AfterCommitAsync(
            IDocumentSession session,
            IChangeSet changes,
            CancellationToken ct)
        {
            LastChangeSet = changes.Clone();
            return Task.CompletedTask;
        }
    }

    public sealed class LongVersionedDocument : IVersioned
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Version { get; set; }
    }

    public sealed class TenantVersionedDocument : Record, IVersioned
    {
        public string Name { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public long Version { get; set; }
    }
}
