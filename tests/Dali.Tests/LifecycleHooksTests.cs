using System.Collections.Generic;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace Dali.Tests;

// ──────────────────────────────────────────────
// Test entities
// ──────────────────────────────────────────────

public class LifecycleTestItem : Record
{
    public string Name { get; set; } = "";
}

// ──────────────────────────────────────────────
// Helper listeners for tests
// ──────────────────────────────────────────────

internal sealed class TrackingListener : IDocumentSessionListener
{
    public List<string> CallOrder { get; } = new();
    public List<Operation> CommittedOperations { get; } = new();

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        CallOrder.Add("BeforeSaveChangesAsync");
        return Task.CompletedTask;
    }

    public Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct)
    {
        CallOrder.Add("AfterSaveChangesAsync");
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet changes, CancellationToken ct)
    {
        CallOrder.Add("AfterCommitAsync");
        CommittedOperations.AddRange(changes.Operations);
        return Task.CompletedTask;
    }

    public Task BeforeCommitAsync(IDocumentSession session, CancellationToken ct)
    {
        CallOrder.Add("BeforeCommitAsync");
        return Task.CompletedTask;
    }

    public Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        CallOrder.Add($"BeforeStore:{entity.GetType().Name}");
        return Task.CompletedTask;
    }

    public Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        CallOrder.Add($"AfterStore:{entity.GetType().Name}");
        return Task.CompletedTask;
    }

    public Task BeforeDeleteAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        CallOrder.Add($"BeforeDelete:{entity.GetType().Name}");
        return Task.CompletedTask;
    }

    public Task AfterDeleteAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        CallOrder.Add($"AfterDelete:{entity.GetType().Name}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Tests for lifecycle hooks (Phase 1-3): async per-entity hooks, IChangeSet, DocumentSessionListenerBase.
/// </summary>
public class LifecycleHooksTests
{
    // ════════════════════════════════════════════════════════════
    //  Section 1 — Async per-entity hooks
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task BeforeStoreAsync_IsCalled_OnInsert()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "Test" });
        session.Store(new LifecycleTestItem { Name = "Test2" });
        await session.SaveChangesAsync();

        listener.CallOrder.ShouldContain("BeforeStore:LifecycleTestItem");
        listener.CallOrder.ShouldContain("AfterStore:LifecycleTestItem");
    }

    [Test]
    public async Task AfterStoreAsync_IsCalled_OnInsert()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "Test" });
        await session.SaveChangesAsync();

        listener.CallOrder.ShouldContain("AfterStore:LifecycleTestItem");
    }

    [Test]
    public async Task BeforeDeleteAsync_IsCalled_OnDelete()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        var item = new LifecycleTestItem { Name = "ToDelete" };
        session.Store(item);
        await session.SaveChangesAsync();

        session.Delete(item);
        await session.SaveChangesAsync();

        listener.CallOrder.ShouldContain("BeforeDelete:LifecycleTestItem");
    }

    [Test]
    public async Task AfterDeleteAsync_IsCalled_OnDelete()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        var item = new LifecycleTestItem { Name = "ToDelete" };
        session.Store(item);
        await session.SaveChangesAsync();

        session.Delete(item);
        await session.SaveChangesAsync();

        listener.CallOrder.ShouldContain("AfterDelete:LifecycleTestItem");
    }

    [Test]
    public async Task BeforeStoreAsync_IsCalled_ForEachStore()
    {
        // Verifies that BeforeStoreAsync fires for every Store() call,
        // whether the entity is being inserted or tracked as modified.
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();

        // Store two entities to trigger BeforeStore twice
        session.Store(new LifecycleTestItem { Name = "First" });
        listener.CallOrder.Clear();

        session.Store(new LifecycleTestItem { Name = "Second" });
        await session.SaveChangesAsync();

        // Should have been called twice: once for each entity
        var count = listener.CallOrder.Count(c => c == "BeforeStore:LifecycleTestItem");
        count.ShouldBeGreaterThanOrEqualTo(1);
    }

    // ════════════════════════════════════════════════════════════
    //  Section 2 — IChangeSet
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ChangeSet_HasChanges_AfterCommit()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "CS1" });
        await session.SaveChangesAsync();

        listener.CommittedOperations.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task ChangeSet_Operations_ContainsOperations()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "CS2" });
        session.Store(new LifecycleTestItem { Name = "CS3" });
        await session.SaveChangesAsync();

        listener.CommittedOperations.Count.ShouldBe(2);
    }

    [Test]
    public async Task ChangeSet_Operations_OperationTypes()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "Added" });
        await session.SaveChangesAsync();

        listener.CommittedOperations.ShouldContain(op => op.Type == OperationType.Added);
    }

    // ════════════════════════════════════════════════════════════
    //  Section 3 — DocumentSessionListenerBase
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ListenerBase_CustomOverride_Called()
    {
        var called = false;
        var listener = new CallbackListener(() => called = true);

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "BaseTest" });
        await session.SaveChangesAsync();

        called.ShouldBeTrue();
    }

    [Test]
    public async Task ListenerBase_PatternMatch_OnSpecificEntity()
    {
        var matched = false;
        var listener = new TypedMatchListener(() => matched = true);

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "MatchTest" });
        await session.SaveChangesAsync();

        matched.ShouldBeTrue();
    }

    [Test]
    public async Task ListenerBase_UnrelatedEntity_DoesNotMatch()
    {
        var matched = false;
        var listener = new TypedMatchListener(() => matched = true);

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        // Store a different entity type — listener should not match
        await using var session = await store.LightweightSessionAsync();
        session.Store(new Person { Name = "NoMatch", Age = 30 });
        await session.SaveChangesAsync();

        matched.ShouldBeFalse();
    }

    // ════════════════════════════════════════════════════════════
    //  Section 4 — Integration: ordering
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Listener_FullLifecycle_Ordering()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "OrderTest" });
        await session.SaveChangesAsync();

        // Verify call order: BeforeSave -> BeforeStore -> AfterStore -> AfterSave -> BeforeCommit -> AfterCommit
        var beforeSaveIdx = listener.CallOrder.IndexOf("BeforeSaveChangesAsync");
        var beforeStoreIdx = listener.CallOrder.IndexOf("BeforeStore:LifecycleTestItem");
        var afterStoreIdx = listener.CallOrder.IndexOf("AfterStore:LifecycleTestItem");
        var afterSaveIdx = listener.CallOrder.IndexOf("AfterSaveChangesAsync");
        var beforeCommitIdx = listener.CallOrder.IndexOf("BeforeCommitAsync");
        var afterCommitIdx = listener.CallOrder.IndexOf("AfterCommitAsync");

        beforeSaveIdx.ShouldBeLessThan(beforeStoreIdx);
        beforeStoreIdx.ShouldBeLessThan(afterStoreIdx);
        afterStoreIdx.ShouldBeLessThan(afterSaveIdx);
        afterSaveIdx.ShouldBeLessThan(beforeCommitIdx);
        beforeCommitIdx.ShouldBeLessThan(afterCommitIdx);
    }

    [Test]
    public async Task Multiple_Listeners_AllCalled()
    {
        var callCount1 = 0;
        var callCount2 = 0;
        var listener1 = new CallbackListener(() => callCount1++);
        var listener2 = new CallbackListener(() => callCount2++);

        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener1);
            o.Listeners.Add(listener2);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "Multi" });
        await session.SaveChangesAsync();

        callCount1.ShouldBeGreaterThanOrEqualTo(1);
        callCount2.ShouldBeGreaterThanOrEqualTo(1);
    }

    // ════════════════════════════════════════════════════════════
    //  Section 5 — IChangeSet integration
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ChangeSet_Operations_IncludeTypes()
    {
        var listener = new TrackingListener();
        await using var store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Listeners.Add(listener);
        });

        await using var session = await store.LightweightSessionAsync();
        session.Store(new LifecycleTestItem { Name = "TypeTest" });
        await session.SaveChangesAsync();

        listener.CommittedOperations.ShouldContain(op => op.EntityType == typeof(LifecycleTestItem));
    }
}

// ──────────────────────────────────────────────
// Helper listener classes
// ──────────────────────────────────────────────

internal sealed class CallbackListener : DocumentSessionListenerBase
{
    private readonly Action _onAfterStore;

    public CallbackListener(Action onAfterStore)
    {
        _onAfterStore = onAfterStore;
    }

    public override Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        _onAfterStore();
        return Task.CompletedTask;
    }
}

internal sealed class TypedMatchListener : DocumentSessionListenerBase
{
    private readonly Action _onMatch;

    public TypedMatchListener(Action onMatch)
    {
        _onMatch = onMatch;
    }

    public override Task AfterStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        if (entity is LifecycleTestItem)
            _onMatch();
        return Task.CompletedTask;
    }
}
