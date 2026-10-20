using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Exercises the transaction lifetime rules around finite record mutations and
/// optimistic-concurrency fences. These deliberately use separate sessions for
/// verification so a failed transaction cannot be mistaken for an identity-map
/// artifact.
/// </summary>
public sealed class ValidatedMutationLifecycleTests
{
    [Test]
    public async Task Explicit_fence_then_same_table_typed_mutation_is_rejected_on_the_second_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenced = RecordId.From("versioned_person", "lifecycle-typed-fence");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() => session.QueueRecordMutation(
            ValidatedRecordMutation.Create(
                RecordId.From("versioned_person", "lifecycle-typed-write"),
                new Dictionary<string, object?> { ["name"] = "must-not-queue" })));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fenced)).ShouldBe(1);
        await AssertRecordDoesNotExistAsync(store, RecordId.From("versioned_person", "lifecycle-typed-write"));
    }

    [Test]
    public async Task Explicit_fence_then_same_table_document_write_is_rejected_on_the_second_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenced = RecordId.From("versioned_person", "lifecycle-document-fence");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);
        await session.SaveChangesAsync();

        session.Insert(new VersionedPerson
        {
            Id = new RecordIdOf<string>("versioned_person", "lifecycle-document-write"),
            Name = "must-not-write",
            Version = 0
        });
        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fenced)).ShouldBe(1);
        await AssertRecordDoesNotExistAsync(store, RecordId.From("versioned_person", "lifecycle-document-write"));
    }

    [Test]
    public async Task Explicit_fence_then_raw_operation_is_rejected_after_the_first_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenced = RecordId.From("versioned_person", "lifecycle-raw-fence");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);
        await session.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.ExecuteSqlAsync("UPDATE versioned_person SET name = 'must-not-write';"));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fenced)).ShouldBe(1);
    }

    [Test]
    public async Task Explicit_typed_mutation_then_same_table_fence_is_rejected_after_the_first_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var target = RecordId.From("versioned_person", "lifecycle-typed-then-fence");
        await EnsureTableAsync(store, target.Table);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            target,
            new Dictionary<string, object?> { ["name"] = "first write", ["version"] = 1L }));
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(target, 1));

        await transaction.RollbackAsync();
        await AssertRecordDoesNotExistAsync(store, target);
    }

    [Test]
    public async Task Explicit_document_write_then_same_table_fence_is_rejected_after_the_first_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var target = RecordId.From("versioned_person", "lifecycle-document-then-fence");
        await EnsureTableAsync(store, target.Table);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.Insert(new VersionedPerson
        {
            Id = new RecordIdOf<string>("versioned_person", "lifecycle-document-then-fence"),
            Name = "first write",
            Version = 1
        });
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(target, 1));

        await transaction.RollbackAsync();
        await AssertRecordDoesNotExistAsync(store, target);
    }

    [Test]
    public async Task Explicit_opaque_operation_then_fence_is_rejected_after_the_first_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var target = RecordId.From("versioned_person", "lifecycle-opaque-then-fence");
        await SeedVersionAsync(store, target, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.ExecuteSqlAsync("SELECT * FROM versioned_person;");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(target, 1));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, target)).ShouldBe(1);
    }

    [Test]
    public async Task Clear_changes_cannot_erase_an_explicit_transaction_fence_footprint()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenced = RecordId.From("versioned_person", "lifecycle-clear-footprint");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);
        await session.SaveChangesAsync();
        session.ClearChanges();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.ExecuteSqlAsync("UPDATE versioned_person SET name = 'must-not-write';"));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, fenced)).ShouldBe(1);
    }

    [Test]
    public async Task Explicit_disjoint_typed_mutation_and_fence_across_saves_commit_together()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenced = RecordId.From("versioned_person", "lifecycle-disjoint-fence");
        var outbox = RecordId.From("lifecycle_outbox", "lifecycle-disjoint-mutation");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);
        await session.SaveChangesAsync();
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            outbox,
            new Dictionary<string, object?> { ["status"] = "queued" }));
        await session.SaveChangesAsync();
        await transaction.CommitAsync();

        (await ReadVersionAsync(store, fenced)).ShouldBe(2);
        (await ReadStringFieldAsync(store, outbox, "status")).ShouldBe("queued");
    }

    [Test]
    public async Task Implicit_after_commit_cannot_queue_a_mutation_or_leak_it_to_a_later_save()
    {
        var target = RecordId.From("lifecycle_after_commit", "implicit-late-mutation");
        var listener = new AfterCommitMutationListener(target);
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        await EnsureTableAsync(store, target.Table);

        await using var session = await store.LightweightSessionAsync();
        session.Store(new Person { Name = "implicit callback trigger" });
        await session.SaveChangesAsync();

        listener.QueueError.ShouldBeOfType<InvalidOperationException>();
        session.Store(new Person { Name = "later save" });
        await session.SaveChangesAsync();
        await AssertRecordDoesNotExistAsync(store, target);
    }

    [Test]
    public async Task Explicit_after_commit_cannot_queue_a_mutation_or_leak_it_to_a_later_save()
    {
        var target = RecordId.From("lifecycle_after_commit", "explicit-late-mutation");
        var listener = new AfterCommitMutationListener(target);
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        await EnsureTableAsync(store, target.Table);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.Store(new Person { Name = "explicit callback trigger" });
        await session.SaveChangesAsync();
        listener.QueueError.ShouldBeNull();
        await transaction.CommitAsync();

        listener.QueueError.ShouldBeOfType<InvalidOperationException>();
        session.Store(new Person { Name = "later explicit session save" });
        await session.SaveChangesAsync();
        await AssertRecordDoesNotExistAsync(store, target);
    }

    [Test]
    public async Task Raw_operation_from_before_commit_is_rejected_while_a_fence_is_active()
    {
        var listener = new RawBeforeCommitListener();
        await using var store = await TestHarness.CreateStoreAsync(options => options.Listeners.Add(listener));
        var fenced = RecordId.From("versioned_person", "lifecycle-before-commit-raw");
        await SeedVersionAsync(store, fenced, 1);

        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenced, 1);

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        listener.WasCalled.ShouldBeTrue();
        (await ReadVersionAsync(store, fenced)).ShouldBe(1);
    }

    [Test]
    public async Task Duplicate_typed_create_rolls_back_the_business_document_write()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var duplicate = RecordId.From("lifecycle_outbox", "duplicate-create");
        await CreateRawRecordAsync(store, duplicate, new Dictionary<string, object?> { ["status"] = "existing" });
        await EnsureTableAsync(store, "person");
        const string businessId = "duplicate-create-business";

        await using (var session = await store.LightweightSessionAsync())
        {
            session.Insert(new Person
            {
                Id = new RecordIdOf<string>("person", businessId),
                Name = "must-rollback"
            });
            session.QueueRecordMutation(ValidatedRecordMutation.Create(
                duplicate,
                new Dictionary<string, object?> { ["status"] = "duplicate" }));

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }

        await AssertPersonDoesNotExistAsync(store, businessId);
        (await ReadStringFieldAsync(store, duplicate, "status")).ShouldBe("existing");
    }

    [Test]
    public async Task Missing_typed_merge_rolls_back_the_business_document_write()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var missing = RecordId.From("lifecycle_outbox", "missing-merge");
        await EnsureTableAsync(store, missing.Table);
        await EnsureTableAsync(store, "person");
        const string businessId = "missing-merge-business";

        await using (var session = await store.LightweightSessionAsync())
        {
            session.Insert(new Person
            {
                Id = new RecordIdOf<string>("person", businessId),
                Name = "must-rollback"
            });
            session.QueueRecordMutation(ValidatedRecordMutation.MergeExisting(
                missing,
                new Dictionary<string, object?> { ["status"] = "handled" }));

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
        }

        await AssertPersonDoesNotExistAsync(store, businessId);
        await AssertRecordDoesNotExistAsync(store, missing);
    }

    [Test]
    public async Task Default_database_mutation_and_mapped_database_fence_fail_without_writes()
    {
        const string mappedDatabase = "lifecycle_mapped_database";
        var fenceTarget = RecordId.From("versioned_person", "mapped-fence");
        var defaultMutation = RecordId.From("lifecycle_default_outbox", "must-not-write");
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>()
                .Schema(mappedDatabase)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });
        await SeedVersionInDatabaseAsync(store, mappedDatabase, fenceTarget, 4);
        await EnsureTableAsync(store, defaultMutation.Table);

        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenceTarget, 4);
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            defaultMutation,
            new Dictionary<string, object?> { ["status"] = "must-not-write" }));

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        (await ReadVersionInDatabaseAsync(store, mappedDatabase, fenceTarget)).ShouldBe(4);
        await AssertRecordDoesNotExistAsync(store, defaultMutation);
    }

    [Test]
    public async Task Mapped_database_equal_to_the_default_database_can_share_a_save_with_a_disjoint_mutation()
    {
        const string database = "lifecycle_default_database_alias";
        var fenceTarget = RecordId.From("versioned_person", "default-alias-fence");
        var outbox = RecordId.From("lifecycle_default_alias_outbox", "default-alias-mutation");
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Database = database;
            options.Schema.For<VersionedPerson>()
                .Schema(database)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });
        await SeedVersionInDatabaseAsync(store, database, fenceTarget, 1);

        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenceTarget, 1);
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            outbox,
            new Dictionary<string, object?> { ["status"] = "same-physical-database" }));

        await session.SaveChangesAsync();

        (await ReadVersionInDatabaseAsync(store, database, fenceTarget)).ShouldBe(2);
        (await ReadStringFieldAsync(store, outbox, "status")).ShouldBe("same-physical-database");
    }

    [Test]
    public async Task Explicit_same_physical_default_and_mapped_database_history_rejects_a_same_table_fence()
    {
        const string database = "lifecycle_default_history_alias";
        var target = RecordId.From("versioned_person", "default-history-same-table");
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Database = database;
            options.Schema.For<VersionedPerson>()
                .Schema(database)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
        });
        await EnsureTableAsync(store, target.Table);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            target,
            new Dictionary<string, object?> { ["name"] = "first write", ["version"] = 1L }));
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(target, 1));

        await transaction.RollbackAsync();
        await AssertRecordDoesNotExistAsync(store, target);
    }

    [Test]
    public async Task Explicit_patch_then_same_table_fence_is_rejected_after_the_first_save()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var target = RecordId.From("versioned_person", "lifecycle-patch-then-fence");
        await SeedVersionAsync(store, target, 1);

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.Patch<VersionedPerson>("lifecycle-patch-then-fence")
            .Set(person => person.Name, "must-roll-back");
        await session.SaveChangesAsync();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<VersionedPerson>(target, 1));

        await transaction.RollbackAsync();
        (await ReadVersionAsync(store, target)).ShouldBe(1);
        (await ReadStringFieldAsync(store, target, "name")).ShouldBe("seed");
    }

    [Test]
    public async Task Late_before_commit_default_database_mutation_is_rejected_with_a_mapped_database_fence()
    {
        const string mappedDatabase = "lifecycle_late_hook_mapped";
        var fenceTarget = RecordId.From("versioned_person", "late-hook-fence");
        var defaultMutation = RecordId.From("lifecycle_late_hook_default", "must-not-write");
        var listener = new LateMutationBeforeCommitListener(defaultMutation);
        await using var store = await TestHarness.CreateStoreAsync(options =>
        {
            options.Schema.For<VersionedPerson>()
                .Schema(mappedDatabase)
                .SetSchemaMode(SchemaMode.Flexible);
            options.Schema.AutoCreateDatabases = true;
            options.Listeners.Add(listener);
        });
        await SeedVersionInDatabaseAsync(store, mappedDatabase, fenceTarget, 1);
        await EnsureTableAsync(store, defaultMutation.Table);
        await EnsureTableAsync(store, defaultMutation.Table, mappedDatabase);

        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenceTarget, 1);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());

        listener.WasCalled.ShouldBeTrue();
        exception.GetBaseException().Message.ShouldContain("Cross-database");
        (await ReadVersionInDatabaseAsync(store, mappedDatabase, fenceTarget)).ShouldBe(1);
        await AssertRecordDoesNotExistAsync(store, defaultMutation);
        await AssertRecordDoesNotExistAsync(store, defaultMutation, mappedDatabase);
    }

    private static async Task SeedVersionAsync(IDocumentStore store, RecordId record, long version)
        => await SeedVersionInDatabaseAsync(store, null, record, version);

    private static async Task SeedVersionInDatabaseAsync(
        IDocumentStore store,
        string? database,
        RecordId record,
        long version)
        => await CreateRawRecordAsync(
            store,
            record,
            new Dictionary<string, object?> { ["name"] = "seed", ["version"] = version },
            database);

    private static async Task CreateRawRecordAsync(
        IDocumentStore store,
        RecordId record,
        IReadOnlyDictionary<string, object?> content,
        string? database = null)
    {
        await EnsureTableAsync(store, record.Table, database);
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var target = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await target.RawQuery(
            "CREATE $record CONTENT $content;",
            new Dictionary<string, object?> { ["record"] = record, ["content"] = content });
        response.EnsureAllOks();
    }

    private static async Task EnsureTableAsync(IDocumentStore store, string table, string? database = null)
    {
        if (string.IsNullOrWhiteSpace(table)
            || table.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("The test table name must be a simple identifier.", nameof(table));
        }

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var target = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await target.RawQuery($"DEFINE TABLE IF NOT EXISTS {table} SCHEMALESS;");
        response.EnsureAllOks();
    }

    private static async Task<long> ReadVersionAsync(IDocumentStore store, RecordId record)
        => await ReadVersionInDatabaseAsync(store, null, record);

    private static async Task<long> ReadVersionInDatabaseAsync(
        IDocumentStore store,
        string? database,
        RecordId record)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var target = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await target.RawQuery(
            "SELECT VALUE version FROM $record;",
            new Dictionary<string, object?> { ["record"] = record });
        response.EnsureAllOks();
        return (response.GetValue<List<long>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task<string> ReadStringFieldAsync(
        IDocumentStore store,
        RecordId record,
        string field)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            $"SELECT VALUE {field} FROM $record;",
            new Dictionary<string, object?> { ["record"] = record });
        response.EnsureAllOks();
        return (response.GetValue<List<string>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task AssertRecordDoesNotExistAsync(
        IDocumentStore store,
        RecordId record,
        string? database = null)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var target = database is null
            ? session.Session
            : await session.GetSessionForSchemaAsync(database);
        var response = await target.RawQuery(
            "SELECT * FROM $record;",
            new Dictionary<string, object?> { ["record"] = record });
        response.EnsureAllOks();
        (response.GetValue<List<object>>(0) ?? []).ShouldBeEmpty();
    }

    private static async Task AssertPersonDoesNotExistAsync(IDocumentStore store, string id)
    {
        await using var verify = await store.LightweightSessionAsync();
        (await verify.LoadAsync<Person>(id)).ShouldBeNull();
    }

    private sealed class AfterCommitMutationListener(RecordId target) : DocumentSessionListenerBase
    {
        public Exception? QueueError { get; private set; }

        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
        {
            try
            {
                session.QueueRecordMutation(ValidatedRecordMutation.Create(
                    target,
                    new Dictionary<string, object?> { ["status"] = "must-not-leak" }));
            }
            catch (Exception exception)
            {
                QueueError = exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RawBeforeCommitListener : DocumentSessionListenerBase
    {
        public bool WasCalled { get; private set; }

        public override async Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
        {
            WasCalled = true;
            await session.ExecuteSqlAsync(
                "UPDATE versioned_person SET name = 'must-not-write';",
                null,
                ct);
        }
    }

    private sealed class LateMutationBeforeCommitListener(RecordId target) : DocumentSessionListenerBase
    {
        public bool WasCalled { get; private set; }

        public override Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
        {
            WasCalled = true;
            session.QueueRecordMutation(ValidatedRecordMutation.Create(
                target,
                new Dictionary<string, object?> { ["status"] = "must-not-write" }));
            return Task.CompletedTask;
        }
    }
}
