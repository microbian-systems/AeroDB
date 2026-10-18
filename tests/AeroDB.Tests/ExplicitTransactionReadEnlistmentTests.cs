using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// RED contract for typed reads while a <see cref="DocumentSession"/> owns an
/// explicit transaction. These tests deliberately use fresh no-tracking
/// sessions except for the documented identity-map control.
/// </summary>
public sealed class ExplicitTransactionReadEnlistmentTests
{
    private const string ExplicitDatabase = "explicit_read_enlistment";

    private static async Task<IDocumentStore> CreateStoreAsync()
        => await TestHarness.CreateStoreAsync(options =>
        {
            options.Database = ExplicitDatabase;
            // Keep the table outside the transaction so rollback verifies an
            // absent row, not the strict missing-table error contract.
            options.Schema.For<Person>();
            // This is deliberately a named mapping to the same physical
            // database as the parent session. It must stay transaction-bound.
            options.Schema.For<DefaultEntity>().Schema(ExplicitDatabase);
            options.Schema.For<WarehouseItem>().Schema("explicit_read_other_database");
            options.Schema.AutoCreateDatabases = true;
        });

    private static Person Person(string id, string name) => new()
    {
        Id = new RecordIdOf<string>("person", id),
        Name = name,
        Age = 42,
    };

    [Test]
    [Arguments("load")]
    [Arguments("linq")]
    [Arguments("raw-document")]
    [Arguments("exists")]
    [NotInParallel]
    public async Task Explicit_transaction_each_typed_read_path_sees_local_writes_then_rollback_discards_them(
        string readPath)
    {
        await using var store = await CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        const string id = "explicit-read-local";

        await using var transaction = await session.BeginTransactionAsync();
        session.Store(Person(id, "local-only"));
        await session.SaveChangesAsync();

        switch (readPath)
        {
            case "load":
            {
                var loaded = await session.LoadAsync<Person>(id);
                loaded.ShouldNotBeNull();
                loaded.Name.ShouldBe("local-only");
                break;
            }
            case "linq":
            {
                var queried = await session.Query<Person>().FirstOrDefaultAsync(person => person.Name == "local-only");
                queried.ShouldNotBeNull();
                ((RecordIdOf<string>)queried.Id!).Id.ShouldBe(id);
                break;
            }
            case "raw-document":
            {
                var raw = await session.RawDocumentQueryAsync<Person>(
                    "SELECT * FROM person WHERE name = $name;",
                    new Dictionary<string, object?> { ["name"] = "local-only" },
                    CancellationToken.None);
                raw.Count.ShouldBe(1);
                raw[0].Name.ShouldBe("local-only");
                break;
            }
            case "exists":
                (await session.CheckExistsAsync<Person>(id)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath), readPath, "Unsupported typed read path.");
        }

        await transaction.RollbackAsync();

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(id)).ShouldBeNull();
        (await verify.CheckExistsAsync<Person>(id)).ShouldBeFalse();
    }

    [Test]
    [NotInParallel]
    public async Task Explicit_transaction_pre_cancelled_typed_read_honors_cancellation_and_rolls_back_cleanly()
    {
        await using var store = await CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });
        const string id = "explicit-read-cancelled";

        await using var transaction = await session.BeginTransactionAsync();
        session.Store(Person(id, "local-only"));
        await session.SaveChangesAsync();

        using var cancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.LoadAsync<Person>(id, linkedCancellation.Token));

        await transaction.RollbackAsync();

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.LoadAsync<Person>(id)).ShouldBeNull();
    }

    [Test]
    [Arguments("linq")]
    [Arguments("raw-document")]
    [NotInParallel]
    public async Task Explicit_transaction_named_mapping_to_the_same_database_sees_local_writes_and_rolls_back_with_the_parent(
        string readPath)
    {
        await using var store = await CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });

        await using var transaction = await session.BeginTransactionAsync();
        session.Store(new DefaultEntity { Name = "same-database-local" });
        await session.SaveChangesAsync();

        switch (readPath)
        {
            case "linq":
            {
                var local = await session.Query<DefaultEntity>()
                    .FirstOrDefaultAsync(item => item.Name == "same-database-local");
                local.ShouldNotBeNull();
                local.Name.ShouldBe("same-database-local");
                break;
            }
            case "raw-document":
            {
                var local = await session.RawDocumentQueryAsync<DefaultEntity>(
                    "SELECT * FROM default_entity WHERE name = $name;",
                    new Dictionary<string, object?> { ["name"] = "same-database-local" },
                    CancellationToken.None);
                local.Count.ShouldBe(1);
                local[0].Name.ShouldBe("same-database-local");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath), readPath, "Unsupported typed read path.");
        }

        await transaction.RollbackAsync();

        await using var verify = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });
        (await verify.Query<DefaultEntity>().Where(item => item.Name == "same-database-local").ToListAsync())
            .ShouldBeEmpty();
    }

    [Test]
    [Arguments("linq")]
    [Arguments("raw-document")]
    [NotInParallel]
    public async Task Explicit_transaction_different_database_mapping_fails_closed_before_a_forked_read_can_escape_the_transaction(
        string readPath)
    {
        await using var store = await CreateStoreAsync();
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });

        await using var transaction = await session.BeginTransactionAsync();
        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            switch (readPath)
            {
                case "linq":
                    await session.Query<WarehouseItem>().ToListAsync();
                    break;
                case "raw-document":
                    await session.RawDocumentQueryAsync<WarehouseItem>(
                        "SELECT * FROM warehouse_item;",
                        null,
                        CancellationToken.None);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(readPath), readPath, "Unsupported typed read path.");
            }
        });

        error.Message.ShouldContain("explicit transaction");
        error.Message.ShouldContain("explicit_read_other_database");
        await transaction.RollbackAsync();
    }

    [Test]
    [Arguments("load")]
    [Arguments("linq")]
    [Arguments("raw-document")]
    [Arguments("exists")]
    [NotInParallel]
    public async Task Nontransactional_document_session_keeps_existing_cross_database_read_routing(string readPath)
    {
        await using var store = await CreateStoreAsync();
        var item = new WarehouseItem { Sku = "cross-db-normal", Quantity = 3 };
        await using (var write = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(item);
            await write.SaveChangesAsync();
        }

        var id = item.Id!;
        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None });

        switch (readPath)
        {
            case "load":
                (await session.LoadAsync<WarehouseItem>(id))!.Sku.ShouldBe("cross-db-normal");
                break;
            case "linq":
                (await session.Query<WarehouseItem>().Where(candidate => candidate.Sku == "cross-db-normal").ToListAsync())
                    .Count.ShouldBe(1);
                break;
            case "raw-document":
                (await session.RawDocumentQueryAsync<WarehouseItem>(
                    "SELECT * FROM warehouse_item WHERE sku = $sku;",
                    new Dictionary<string, object?> { ["sku"] = "cross-db-normal" },
                    CancellationToken.None))
                    .Count.ShouldBe(1);
                break;
            case "exists":
                (await session.CheckExistsAsync<WarehouseItem>(id)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath), readPath, "Unsupported typed read path.");
        }
    }

    [Test]
    [Arguments("load")]
    [Arguments("linq")]
    [Arguments("exists")]
    [NotInParallel]
    public async Task Query_session_keeps_existing_cross_database_read_routing(string readPath)
    {
        await using var store = await CreateStoreAsync();
        var item = new WarehouseItem { Sku = "cross-db-query-session", Quantity = 3 };
        await using (var write = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(item);
            await write.SaveChangesAsync();
        }

        var id = item.Id!;
        await using var query = await store.QuerySessionAsync();
        switch (readPath)
        {
            case "load":
                (await query.LoadAsync<WarehouseItem>(id))!.Sku.ShouldBe("cross-db-query-session");
                break;
            case "linq":
                (await query.Query<WarehouseItem>().Where(candidate => candidate.Sku == "cross-db-query-session").ToListAsync())
                    .Count.ShouldBe(1);
                break;
            case "exists":
                (await query.CheckExistsAsync<WarehouseItem>(id)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath), readPath, "Unsupported typed read path.");
        }
    }

    [Test]
    [NotInParallel]
    public async Task A_document_loaded_before_begin_transaction_remains_identity_mapped_and_is_not_a_fresh_transactional_read_contract()
    {
        await using var store = await CreateStoreAsync();
        const string id = "identity-map-before-transaction";
        await using (var write = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            write.Store(Person(id, "original"));
            await write.SaveChangesAsync();
        }

        await using var session = (DocumentSession)await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.IdentityOnly });
        var preloaded = await session.LoadAsync<Person>(id);
        preloaded.ShouldNotBeNull();
        preloaded.Name.ShouldBe("original");

        await using (var peer = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            peer.Store(Person(id, "peer-update"));
            await peer.SaveChangesAsync();
        }

        await using var transaction = await session.BeginTransactionAsync();
        var cached = await session.LoadAsync<Person>(id);
        cached.ShouldBeSameAs(preloaded);
        cached!.Name.ShouldBe("original");
        await transaction.RollbackAsync();
    }
}
