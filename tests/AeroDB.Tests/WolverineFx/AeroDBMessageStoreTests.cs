using AeroDB.WolverineFx;

namespace AeroDB.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using TUnit.Core;
using Wolverine;
using WolverineFx;

[NotInParallel]
public class AeroDBMessageStoreTests
{
    private static async Task<AeroDBMessageStore> CreateStoreAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"AeroDB_test_{Guid.NewGuid():N}.db");
        var client = new SurrealDbKvClient(dbPath);
        await client.Use("test", "test");
        var logger = NullLogger<AeroDBMessageStore>.Instance;
        var store = new AeroDBMessageStore(client, logger);
        await store.InitializeSchemaAsync();
        return store;
    }

    private static Envelope MakeEnvelope(string messageType, byte[]? data = null)
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = messageType
        };
        env.Data = data ?? new byte[] { 1, 2, 3 };
        return env;
    }

    [Test]
    public async Task SchemaInit_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("Test");
        await store.StoreIncomingAsync(env);
        // Success = no exception
    }

    [Test]
    public async Task StoreIncoming_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("StoreTest");
        env.Data = new byte[] { 1, 2, 3 };
        env.Destination = new Uri("AeroDB://localhost/incoming");
        await store.StoreIncomingAsync(env);
    }

    [Test]
    public async Task StoreOutgoing_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("OutgoingTest");
        env.Data = new byte[] { 4, 5, 6 };
        env.Destination = new Uri("AeroDB://localhost/outgoing");
        await store.StoreOutgoingAsync(env, 0);
    }

    [Test]
    public async Task StoreIncoming_Batch_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var envelopes = Enumerable.Range(0, 5).Select(i => MakeEnvelope($"BatchTest-{i}")).ToList();
        await store.StoreIncomingAsync((IReadOnlyList<Envelope>)envelopes);
    }

    [Test]
    public async Task MarkIncomingEnvelopeAsHandled_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("HandleTest");
        await store.StoreIncomingAsync(env);
        await store.MarkIncomingEnvelopeAsHandledAsync(env);
    }

    [Test]
    public async Task MoveToDeadLetterStorage_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("DeadTest");
        env.Destination = new Uri("AeroDB://localhost/queue");
        await store.StoreIncomingAsync(env);
        await store.MoveToDeadLetterStorageAsync(env, new InvalidOperationException("test"));
    }

    [Test]
    public async Task IncrementIncomingEnvelopeAttempts_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("AttemptTest");
        env.Attempts = 5;
        await store.StoreIncomingAsync(env);
        env.Attempts = 6;
        await store.IncrementIncomingEnvelopeAttemptsAsync(env);
    }

    [Test]
    public async Task DeleteOutgoing_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var uri = new Uri("AeroDB://localhost/delete-test");
        var env = MakeEnvelope("DeleteMe");
        env.Destination = uri;
        await store.StoreOutgoingAsync(env, 0);
        await store.DeleteOutgoingAsync(env);
    }

    [Test]
    public async Task ClearAll_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        await store.StoreIncomingAsync(MakeEnvelope("ClearTest"));
        await store.Admin.ClearAllAsync();
    }

    [Test]
    public async Task ScheduleExecution_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("ScheduledTest");
        env.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        await store.StoreIncomingAsync(env);
        await store.ScheduleExecutionAsync(env);
    }

    [Test]
    public async Task StoreIncoming_MultipleCalls_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env1 = MakeEnvelope("Multi1");
        var env2 = MakeEnvelope("Multi2");
        await store.StoreIncomingAsync(env1);
        await store.StoreIncomingAsync(env2);
    }

    [Test]
    public async Task ReleaseIncoming_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("ReleaseTest");
        env.Destination = new Uri("AeroDB://localhost/listener");
        await store.StoreIncomingAsync(env);
        await store.Inbox.ReleaseIncomingAsync(0, new Uri("AeroDB://localhost/listener"));
    }

    [Test]
    public async Task FetchCounts_DoesNotThrow()
    {
        await using var store = await CreateStoreAsync();
        var env = MakeEnvelope("CountTest");
        await store.StoreIncomingAsync(env);
        var counts = await store.Admin.FetchCountsAsync();
        counts.ShouldNotBeNull();
    }
}
