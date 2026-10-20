using AeroDB.Sable;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

public sealed class ValidatedRecordMutationTests
{
    [Test]
    public async Task Fence_and_disjoint_typed_create_commit_together()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        var fenceTarget = RecordId.From("versioned_person", "typed-disjoint-fence");
        await SeedVersionAsync(store, fenceTarget, 1);
        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>(fenceTarget, 1);
        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            RecordId.From("validated_outbox", "typed-disjoint"),
            new Dictionary<string, object?> { ["status"] = "queued" }));

        (await session.SaveChangesAsync()).ShouldBe(2);
    }

    [Test]
    public async Task Same_table_typed_mutation_and_fence_are_rejected_in_both_orders()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var first = await store.LightweightSessionAsync();
        first.FenceExpectedVersion<VersionedPerson>("typed-same-first", 1);
        Should.Throw<InvalidOperationException>(() => first.QueueRecordMutation(
            ValidatedRecordMutation.Create(RecordId.From("versioned_person", "different-id"),
                new Dictionary<string, object?> { ["name"] = "unsafe" })));

        await using var second = await store.LightweightSessionAsync();
        second.QueueRecordMutation(ValidatedRecordMutation.Create(
            RecordId.From("versioned_person", "different-id"),
            new Dictionary<string, object?> { ["name"] = "unsafe" }));
        Should.Throw<InvalidOperationException>(() =>
            second.FenceExpectedVersion<VersionedPerson>("typed-same-second", 1));
    }

    [Test]
    public async Task Raw_execute_and_query_are_rejected_after_a_fence_is_queued()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        session.FenceExpectedVersion<VersionedPerson>("raw-after-fence", 1);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.ExecuteSqlAsync("UPDATE versioned_person SET name = 'unsafe';"));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.RawQueryAsync<object>("SELECT * FROM versioned_person;"));
    }

    [Test]
    public async Task Merge_existing_missing_record_fails_closed()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        session.QueueRecordMutation(ValidatedRecordMutation.MergeExisting(
            RecordId.From("validated_outbox", "missing-merge"),
            new Dictionary<string, object?> { ["status"] = "handled" }));

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync());
    }

    [Test]
    public async Task Queued_create_persists_bound_clr_content()
    {
        await using var store = await TestHarness.CreateStoreAsync();
        await using var session = await store.LightweightSessionAsync();
        var happenedAt = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var messageId = Guid.NewGuid();

        session.QueueRecordMutation(ValidatedRecordMutation.Create(
            RecordId.From("validated_outbox", "create-content"),
            new Dictionary<string, object?>
            {
                ["happened_at"] = happenedAt,
                ["message_id"] = messageId,
                ["body"] = Convert.ToBase64String([1, 2, 3]),
                ["attempts"] = 2L
            }));

        (await session.SaveChangesAsync()).ShouldBe(1);

        var response = await ((DocumentSession)session).Session.RawQuery(
            "SELECT VALUE happened_at FROM $record;",
            new Dictionary<string, object?>
            {
                ["record"] = RecordId.From("validated_outbox", "create-content")
            });
        response.EnsureAllOks();
        (response.GetValue<List<DateTimeOffset>>(0) ?? []).ShouldHaveSingleItem().ShouldBe(happenedAt);
    }

    [Test]
    public void Factories_copy_target_and_content_and_reject_id_writes()
    {
        var original = new Dictionary<string, object?> { ["status"] = "queued" };
        var target = RecordId.From("validated_outbox", "copied-target");
        var mutation = ValidatedRecordMutation.Create(
            target, original);
        original["status"] = "mutated-after-queue";
        original["another"] = 1;

        mutation.Target.ShouldNotBeSameAs(target);
        mutation.Target.ShouldBe(RecordId.From("validated_outbox", "copied-target"));
        mutation.Content.ShouldHaveSingleItem();
        mutation.Content["status"].ShouldBe("queued");
        mutation.Kind.ShouldBe(ValidatedRecordMutationKind.Create);

        Should.Throw<ArgumentException>(() => ValidatedRecordMutation.MergeExisting(
            RecordId.From("validated_outbox", "forbidden-id"),
            new Dictionary<string, object?> { ["ID"] = "forged" }));
    }

    [Test]
    public void Factories_reject_null_or_empty_content()
    {
        Should.Throw<ArgumentNullException>(() => ValidatedRecordMutation.Create(
            RecordId.From("validated_outbox", "null-content"), null!));
        Should.Throw<ArgumentException>(() => ValidatedRecordMutation.Create(
            RecordId.From("validated_outbox", "empty-content"),
            new Dictionary<string, object?>()));
    }

    private static async Task SeedVersionAsync(IDocumentStore store, RecordId record, long version)
    {
        await using var seed = (DocumentSession)await store.LightweightSessionAsync();
        var response = await seed.Session.RawQuery(
            "CREATE $record CONTENT { name: 'seed', version: $version };",
            new Dictionary<string, object?> { ["record"] = record, ["version"] = version });
        response.EnsureAllOks();
    }
}
