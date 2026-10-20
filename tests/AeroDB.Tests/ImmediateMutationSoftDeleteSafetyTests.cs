using AeroDB.Sable;
using SurrealDb.Net.Exceptions.Response;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Regression coverage for the immediate soft-delete API and the shared managed
/// immediate-mutation boundary. The direct boundary injections below model an
/// outcome that becomes unknown after a real statement has been dispatched;
/// they are not a substitute for a driver transport test.
/// </summary>
public sealed class ImmediateMutationSoftDeleteSafetyTests
{
    [Test]
    public async Task Fence_then_SoftDeleteAsync_is_rejected_and_leaves_the_target_unchanged()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedSoftVersionedAsync(store, "soft-delete-fence", "fence");
        await SeedSoftVersionedAsync(store, "soft-delete-target", "target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        session.FenceExpectedVersion<SoftDeleteVersionedPerson>(fence, 1);
        await session.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.SoftDeleteAsync<SoftDeleteVersionedPerson>("soft-delete-target"));

        await transaction.RollbackAsync();
        (await ReadDeletedAsync(store, "soft-delete-target")).ShouldBeFalse();
    }

    [Test]
    public async Task SoftDeleteAsync_then_fence_is_rejected_in_the_same_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedSoftVersionedAsync(store, "soft-delete-before-fence", "fence");
        await SeedSoftVersionedAsync(store, "soft-delete-before-target", "target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.SoftDeleteAsync<SoftDeleteVersionedPerson>("soft-delete-before-target");

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<SoftDeleteVersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await ReadDeletedAsync(store, "soft-delete-before-target")).ShouldBeFalse();
    }

    [Test]
    public async Task ClearChanges_does_not_erase_a_SoftDeleteAsync_footprint()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedSoftVersionedAsync(store, "soft-delete-clear-fence", "fence");
        await SeedSoftVersionedAsync(store, "soft-delete-clear-target", "target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.SoftDeleteAsync<SoftDeleteVersionedPerson>("soft-delete-clear-target");
        session.ClearChanges();

        Should.Throw<InvalidOperationException>(() =>
            session.FenceExpectedVersion<SoftDeleteVersionedPerson>(fence, 1));

        await transaction.RollbackAsync();
        (await ReadDeletedAsync(store, "soft-delete-clear-target")).ShouldBeFalse();
    }

    [Test]
    public async Task SoftDeleteAsync_rolls_back_with_the_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedSoftVersionedAsync(store, "soft-delete-rollback", "target");

        await using var session = await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.SoftDeleteAsync<SoftDeleteVersionedPerson>("soft-delete-rollback");
        await transaction.RollbackAsync();

        (await ReadDeletedAsync(store, "soft-delete-rollback")).ShouldBeFalse();
    }

    [Test]
    public async Task Post_dispatch_transport_failure_poison_the_transaction_until_rollback()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "post-dispatch-target", "before");

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var failure = await Should.ThrowAsync<IOException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                async (targetSession, cancellationToken) =>
                {
                    var response = await EmbeddedTransactionRawQuery.ExecuteAsync(
                        targetSession,
                        "UPDATE $record SET name = $name;",
                        new Dictionary<string, object?>
                        {
                            ["record"] = RecordId.From("versioned_person", "post-dispatch-target"),
                            ["name"] = "changed-before-transport-failure"
                        },
                        cancellationToken);
                    response.EnsureAllOks();
                    throw new IOException("synthetic post-dispatch transport failure");
                },
                CancellationToken.None));
        failure.Message.ShouldContain("post-dispatch");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<VersionedPerson>()
                .Where(person => person.Name == "before")
                .DeleteAsync());

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "post-dispatch-target")).ShouldNotBeNull()
            .Name.ShouldBe("before");
    }

    [Test]
    public async Task Checked_statement_error_poison_the_transaction_until_rollback()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "checked-error-target", "before");

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<ResponseUnsuccessfulException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                (_, _) => Task.FromResult(
                    new SurrealDbResponse([new ErrorResult()])),
                CancellationToken.None));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<VersionedPerson>()
                .Where(person => person.Name == "before")
                .DeleteAsync());

        await transaction.RollbackAsync();
        (await LoadVersionedAsync(store, "checked-error-target")).ShouldNotBeNull();
    }

    [Test]
    public async Task Pre_canceled_immediate_mutation_does_not_poison_the_explicit_transaction()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fence = await SeedVersionedAsync(store, "pre-canceled-fence", "fence");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                (_, _) =>
                {
                    invoked = true;
                    return Task.FromResult(new SurrealDbResponse([]));
                },
                cancellation.Token));
        invoked.ShouldBeFalse();

        session.FenceExpectedVersion<VersionedPerson>(fence, 1).ShouldNotBeNull();

        await transaction.RollbackAsync();
    }

    [Test]
    public async Task Autocommit_post_dispatch_transport_failure_has_an_unknown_outcome_with_the_original_cause()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "autocommit-io", "before");
        var transportFailure = new IOException("synthetic autocommit transport failure");

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var exception = await Should.ThrowAsync<ImmediateMutationOutcomeUnknownException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                async (targetSession, cancellationToken) =>
                {
                    await DispatchNameUpdateAsync(
                        targetSession,
                        "autocommit-io",
                        "changed-before-transport-failure",
                        cancellationToken);
                    throw transportFailure;
                },
                CancellationToken.None));

        exception.InnerException.ShouldBeSameAs(transportFailure);
        exception.CallerCancellationToken.ShouldBe(CancellationToken.None);
        (await LoadVersionedAsync(store, "autocommit-io")).ShouldNotBeNull()
            .Name.ShouldBe("changed-before-transport-failure");
    }

    [Test]
    public async Task Autocommit_post_dispatch_cancellation_has_an_unknown_outcome_and_preserves_the_caller_token()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "autocommit-cancel", "before");
        using var cancellation = new CancellationTokenSource();
        var cancellationFailure = new OperationCanceledException(
            "synthetic post-dispatch cancellation",
            cancellation.Token);

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var exception = await Should.ThrowAsync<ImmediateMutationOutcomeUnknownException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                async (targetSession, cancellationToken) =>
                {
                    await DispatchNameUpdateAsync(
                        targetSession,
                        "autocommit-cancel",
                        "changed-before-cancellation",
                        cancellationToken);
                    throw cancellationFailure;
                },
                cancellation.Token));

        exception.InnerException.ShouldBeSameAs(cancellationFailure);
        exception.CallerCancellationToken.ShouldBe(cancellation.Token);
        (await LoadVersionedAsync(store, "autocommit-cancel")).ShouldNotBeNull()
            .Name.ShouldBe("changed-before-cancellation");
    }

    [Test]
    public async Task Autocommit_post_dispatch_timeout_has_an_unknown_outcome_with_the_original_cause()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await SeedVersionedAsync(store, "autocommit-timeout", "before");
        var timeoutFailure = new TimeoutException("synthetic post-dispatch timeout");

        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var exception = await Should.ThrowAsync<ImmediateMutationOutcomeUnknownException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                async (targetSession, cancellationToken) =>
                {
                    await DispatchNameUpdateAsync(
                        targetSession,
                        "autocommit-timeout",
                        "changed-before-timeout",
                        cancellationToken);
                    throw timeoutFailure;
                },
                CancellationToken.None));

        exception.InnerException.ShouldBeSameAs(timeoutFailure);
        exception.CallerCancellationToken.ShouldBe(CancellationToken.None);
        (await LoadVersionedAsync(store, "autocommit-timeout")).ShouldNotBeNull()
            .Name.ShouldBe("changed-before-timeout");
    }

    [Test]
    public async Task Autocommit_checked_statement_error_remains_a_confirmed_error()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = (DocumentSession)await store.LightweightSessionAsync();

        var exception = await Should.ThrowAsync<ResponseUnsuccessfulException>(() =>
            session.ExecuteImmediateMutationAsync(
                typeof(VersionedPerson),
                (_, _) => Task.FromResult(new SurrealDbResponse([new ErrorResult()])),
                CancellationToken.None));

        exception.ShouldNotBeOfType<ImmediateMutationOutcomeUnknownException>();
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

    private static async Task<bool> ReadDeletedAsync(IDocumentStore store, string id)
    {
        await using var session = (DocumentSession)await store.LightweightSessionAsync();
        var response = await session.Session.RawQuery(
            "SELECT VALUE deleted FROM $record;",
            new Dictionary<string, object?>
            {
                ["record"] = RecordId.From("soft_delete_versioned_person", id)
            });
        response.EnsureAllOks();
        return (response.GetValue<List<bool>>(0) ?? []).ShouldHaveSingleItem();
    }

    private static async Task<VersionedPerson?> LoadVersionedAsync(IDocumentStore store, string id)
    {
        await using var session = await store.LightweightSessionAsync();
        return await session.LoadAsync<VersionedPerson>(id);
    }

    private static async Task DispatchNameUpdateAsync(
        SurrealDb.Net.ISurrealDbSession targetSession,
        string id,
        string name,
        CancellationToken cancellationToken)
    {
        var response = await EmbeddedTransactionRawQuery.ExecuteAsync(
            targetSession,
            "UPDATE $record SET name = $name;",
            new Dictionary<string, object?>
            {
                ["record"] = RecordId.From("versioned_person", id),
                ["name"] = name
            },
            cancellationToken);
        response.EnsureAllOks();
    }

    private sealed class ErrorResult : ISurrealDbErrorResult
    {
        public bool IsOk => false;
    }
}
