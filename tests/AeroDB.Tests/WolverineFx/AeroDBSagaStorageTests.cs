using AeroDB.WolverineFx;

namespace AeroDB.Tests;

using System.Reflection;
using Sable;
using global::AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SurrealDb.Embedded.InMemory;
using SurrealDb.Net;
using SurrealDb.Net.Models;
using TUnit.Core;
using Wolverine;
using Wolverine.Persistence.Sagas;

// ──────────────────────────────────────────────
// Test Saga Models
// ──────────────────────────────────────────────

/// <summary>Saga with a string Id, used for basic CRUD tests.</summary>
public class TestSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Counter { get; set; }
}

/// <summary>Saga with a Guid Id, used to verify Guid-based saga ID works.</summary>
public class GuidIdSaga : Saga
{
    // Id as string so GetEntityId (which does .ToString()) picks it up;
    // the SagaStorage uses TId = Guid, converting to string for SurrealDB.
    public string Id { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public int Value { get; set; }
}

/// <summary>
/// Saga with explicit RecordId to test TId = string path with a custom Id field.
/// The SagaStorage's LoadAsync queries by record-id directly, so the saga
/// type needs a field that GetEntityId can read.
/// </summary>
public class StringIdSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

// ──────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────

[NotInParallel]
public class AeroDBSagaStorageTests
{
    private static async Task<(IDocumentStore Store, AeroDBSagaStorage<string, TestSaga> Storage)> CreateFixtureAsync()
    {
        var client = new SurrealDbMemoryClient();
        await client.Use("test", "test");

        var store = Documents.For(o =>
        {
            o.ClientFactory = () => client;
        });

        await store.InitializeAsync();

        var storage = new AeroDBSagaStorage<string, TestSaga>(store);
        return (store, storage);
    }

    private static async Task<(IDocumentStore Store, AeroDBSagaStorage<TId, TSaga> Storage)> CreateFixtureForAsync<TId, TSaga>()
        where TSaga : Saga
    {
        var client = new SurrealDbMemoryClient();
        await client.Use("test", "test");

        var store = Documents.For(o =>
        {
            o.ClientFactory = () => client;
        });

        await store.InitializeAsync();

        var storage = new AeroDBSagaStorage<TId, TSaga>(store);
        return (store, storage);
    }

    // ─── LoadAsync ───

    [Test]
    public async Task LoadAsync_ReturnsSaga_WhenExists()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        var sagaId = Guid.NewGuid().ToString();
        var saga = new TestSaga
        {
            Id = sagaId,
            Name = "Loadable Saga",
            Counter = 42
        };

        // Insert directly via session to seed data
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(saga);
            await session.SaveChangesAsync();
        }

        // Load via saga storage
        var loaded = await storage.LoadAsync(sagaId, CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Loadable Saga");
        loaded.Counter.ShouldBe(42);
    }

    [Test]
    public async Task LoadAsync_ReturnsNull_WhenNotExists()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        // Force creation of the test_saga table by inserting and removing a seed record.
        // Without this, SurrealDB throws NotSupportedException when querying a non-existent table.
        await using (var seed = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            seed.Store(new TestSaga { Id = "_init_", Name = "init" });
            await seed.SaveChangesAsync();
            seed.Delete(new TestSaga { Id = "_init_" });
            await seed.SaveChangesAsync();
        }

        var loaded = await storage.LoadAsync("non-existent-id", CancellationToken.None);

        loaded.ShouldBeNull();
    }

    // ─── InsertAsync ───

    [Test]
    public async Task InsertAsync_StoresNewSaga()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        var sagaId = Guid.NewGuid().ToString();
        var saga = new TestSaga
        {
            Id = sagaId,
            Name = "Inserted Saga",
            Counter = 99
        };

        // Insert via saga storage
        await storage.InsertAsync(saga, CancellationToken.None);

        // Verify via query session
        await using var qs = await store.QuerySessionAsync();
        var results = await qs.RawQueryAsync<TestSaga>(
            $"SELECT * FROM test_saga:`{sagaId}`");
        var loaded = results.FirstOrDefault();

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Inserted Saga");
        loaded.Counter.ShouldBe(99);
    }

    // ─── UpdateAsync ───

    [Test]
    public async Task UpdateAsync_UpdatesExistingSaga()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        var sagaId = Guid.NewGuid().ToString();
        var saga = new TestSaga
        {
            Id = sagaId,
            Name = "Original",
            Counter = 10
        };

        // Insert directly
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(saga);
            await session.SaveChangesAsync();
        }

        // Modify and update via saga storage
        saga.Name = "Updated";
        saga.Counter = 20;
        await storage.UpdateAsync(saga, CancellationToken.None);

        // Verify
        await using var qs = await store.QuerySessionAsync();
        var results = await qs.RawQueryAsync<TestSaga>(
            $"SELECT * FROM test_saga:`{sagaId}`");
        var loaded = results.FirstOrDefault();

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Updated");
        loaded.Counter.ShouldBe(20);
    }

    // ─── DeleteAsync ───

    [Test]
    public async Task DeleteAsync_RemovesSaga()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        var sagaId = Guid.NewGuid().ToString();
        var saga = new TestSaga
        {
            Id = sagaId,
            Name = "ToDelete",
            Counter = 7
        };

        // Insert directly
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(saga);
            await session.SaveChangesAsync();
        }

        // Verify it exists
        await using (var qs = await store.QuerySessionAsync())
        {
            var before = await qs.RawQueryAsync<TestSaga>(
                $"SELECT * FROM test_saga:`{sagaId}`");
            before.ShouldNotBeEmpty();
        }

        // Delete via saga storage
        await storage.DeleteAsync(saga, CancellationToken.None);

        // Verify deletion
        await using (var qs = await store.QuerySessionAsync())
        {
            var after = await qs.RawQueryAsync<TestSaga>(
                $"SELECT * FROM test_saga:`{sagaId}`");
            after.ShouldBeEmpty();
        }
    }

    // ─── SaveChangesAsync ───

    [Test]
    public async Task SaveChangesAsync_DoesNotThrow()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;
        await using var storageRef = storage;

        // SaveChangesAsync is a no-op in AeroDBSagaStorage (returns Task.CompletedTask)
        await storage.SaveChangesAsync(CancellationToken.None);
        // Success = no exception
    }

    // ─── DisposeAsync ───

    [Test]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var (store, storage) = await CreateFixtureAsync();
        await using var storeRef = store;

        // DisposeAsync is a no-op in AeroDBSagaStorage
        await storage.DisposeAsync();
        // Success = no exception
    }

    // ─── String ID ───

    [Test]
    public async Task LoadAsync_WithStringId_Works()
    {
        var (store, rawStorage) = await CreateFixtureForAsync<string, StringIdSaga>();
        await using var storeRef = store;
        await using var storageRef = rawStorage;

        var storage = (AeroDBSagaStorage<string, StringIdSaga>)rawStorage;
        var sagaId = Guid.NewGuid().ToString();
        var saga = new StringIdSaga
        {
            Id = sagaId,
            Description = "String ID Saga"
        };

        // Store directly
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(saga);
            await session.SaveChangesAsync();
        }

        // Load
        var loaded = await storage.LoadAsync(sagaId, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded!.Description.ShouldBe("String ID Saga");
    }

    // ─── Guid ID ───

    [Test]
    public async Task LoadAsync_WithGuidId_Works()
    {
        var (store, rawStorage) = await CreateFixtureForAsync<Guid, GuidIdSaga>();
        await using var storeRef = store;
        await using var storageRef = rawStorage;

        var storage = (AeroDBSagaStorage<Guid, GuidIdSaga>)rawStorage;
        var sagaGuid = Guid.NewGuid();
        var sagaId = sagaGuid.ToString();
        var saga = new GuidIdSaga
        {
            Id = sagaId,
            Data = "Guid-ID data",
            Value = 100
        };

        // Store directly
        await using (var session = await store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }))
        {
            session.Store(saga);
            await session.SaveChangesAsync();
        }

        // Load — the storage calls id.ToString() on the Guid parameter,
        // which matches the string stored as the record ID.
        var loaded = await storage.LoadAsync(sagaGuid, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded!.Data.ShouldBe("Guid-ID data");
        loaded.Value.ShouldBe(100);
    }
}
