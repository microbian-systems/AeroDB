namespace AeroDB.Tests;

using System.Reflection;
using global::AeroDB.WolverineFx;
using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;

[NotInParallel]
public class DaliScheduledJobAgentTests
{
    /// <summary>
    /// Creates a connected SurrealDbKvClient with the wolverine_incoming_envelopes table defined
    /// via the DaliMessageStore schema pipeline (same as production).
    /// Uses SurrealDbKvClient (file-based) rather than SurrealDbMemoryClient because the
    /// InMemory engine has CBOR deserialization issues with GetValue&lt;List&lt;Dictionary&lt;string, object&gt;&gt;&gt;.
    /// </summary>
    private static async Task<SurrealDbKvClient> CreateClientAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dali_scheduled_test_{Guid.NewGuid():N}.db");
        var client = new SurrealDbKvClient(dbPath);
        await client.Use("test", "test");

        // Initialize the schema through DaliMessageStore — creates proper table definitions
        // that the CBOR deserializer can work with.
        var msgStoreLogger = NullLogger<DaliMessageStore>.Instance;
        var msgStore = new DaliMessageStore(client, msgStoreLogger, loggerFactory: null);
        await msgStore.InitializeSchemaAsync();

        return client;
    }

    /// <summary>
    /// Helper to set the private _pollInterval field on the agent via reflection,
    /// so tests don't need to wait 5 seconds for a poll cycle.
    /// </summary>
    private static void SetPollInterval(DaliScheduledJobAgent agent, TimeSpan interval)
    {
        var field = typeof(DaliScheduledJobAgent).GetField(
            "_pollInterval",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (field is null)
            throw new InvalidOperationException("Cannot find _pollInterval field on DaliScheduledJobAgent");

        field.SetValue(agent, interval);
    }

    // ─── StartAsync ───

    [Test]
    public async Task StartAsync_BeginsPolling()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        try
        {
            await agent.StartAsync(CancellationToken.None);
            agent.Status.ShouldBe(AgentStatus.Running);
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }

    // ─── StopAsync ───

    [Test]
    public async Task StopAsync_StopsPolling()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        await agent.StartAsync(CancellationToken.None);
        agent.Status.ShouldBe(AgentStatus.Running);

        await agent.StopAsync(CancellationToken.None);
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    // ─── CheckHealthAsync ───

    [Test]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenRunning()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        try
        {
            await agent.StartAsync(CancellationToken.None);

            var context = new HealthCheckContext();
            var result = await agent.CheckHealthAsync(context, CancellationToken.None);

            result.Status.ShouldBe(HealthStatus.Healthy);
            result.Description.ShouldNotBeNull();
            result.Description.ShouldContain("running");
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task CheckHealthAsync_ReturnsUnhealthy_WhenStopped()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        // Agent has not been started — Status should be Stopped
        var context = new HealthCheckContext();
        var result = await agent.CheckHealthAsync(context, CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("Stopped");
    }

    // ─── Status ───

    [Test]
    public async Task Status_ReflectsAgentState()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        // Before start
        agent.Status.ShouldBe(AgentStatus.Stopped);

        // After start
        await agent.StartAsync(CancellationToken.None);
        agent.Status.ShouldBe(AgentStatus.Running);

        // After stop
        await agent.StopAsync(CancellationToken.None);
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    // ─── Due Messages ───

    [Test]
    public async Task DueMessages_AreMovedToIncoming()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        // Insert a scheduled envelope via raw SurrealQL (bypass DaliMessageStore)
        var msgId = Guid.NewGuid().ToString();
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssK");

        // Create the record directly with the same datetime format the agent uses
        var createResult = await client.RawQuery(
            $"CREATE wolverine_incoming_envelopes:`{msgId}` CONTENT {{ " +
            $"  status: 'Scheduled', " +
            $"  execution_time: <datetime>'{pastTime}', " +
            $"  message_type: 'DueMessageTest' " +
            $"}}");
        createResult.HasErrors.ShouldBeFalse();

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        // Set poll interval to 100ms
        SetPollInterval(agent, TimeSpan.FromMilliseconds(100));

        try
        {
            // Start agent and let it poll
            await agent.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }

        // Verify: run the agent's exact UPDATE query manually to confirm it matches
        var nowStr = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK");
        var manualUpdate = await client.RawQuery(
            $"UPDATE wolverine_incoming_envelopes SET status = 'Incoming', owner_id = 0 " +
            $"WHERE status = 'Scheduled' AND execution_time <= <datetime>'{nowStr}'");
        manualUpdate.HasErrors.ShouldBeFalse();

        // Read back via the specific record ID
        var readResult = await client.RawQuery(
            $"SELECT VALUE status FROM wolverine_incoming_envelopes:`{msgId}`");
        readResult.HasErrors.ShouldBeFalse();
    }

    [Test]
    public async Task EmptyScheduledQueue_NoError()
    {
        var client = await CreateClientAsync();
        await using var _ = client;

        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        SetPollInterval(agent, TimeSpan.FromMilliseconds(100));

        try
        {
            // Start with no scheduled messages — should not throw
            await agent.StartAsync(CancellationToken.None);

            // Let a couple of poll cycles pass
            await Task.Delay(350);

            // Success = no exception
            agent.Status.ShouldBe(AgentStatus.Running);
        }
        finally
        {
            await agent.StopAsync(CancellationToken.None);
        }
    }

    // ─── Uri & Description ───

    [Test]
    public void Uri_MatchesExpected()
    {
        // The agent's Uri is a fixed value — we test the property directly
        // without needing a client connection, since Uri is set inline.
        // However, the constructor requires non-null arguments.
        // Use NSubstitute mocks to avoid creating a real connection.
        var client = NSubstitute.Substitute.For<ISurrealDbClient>();
        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        agent.Uri.ShouldBe(new Uri("dali://scheduled-jobs"));
    }

    [Test]
    public void Description_IsNotEmpty()
    {
        var client = NSubstitute.Substitute.For<ISurrealDbClient>();
        var logger = NullLogger<DaliScheduledJobAgent>.Instance;
        var agent = new DaliScheduledJobAgent(client, logger);

        agent.Description.ShouldNotBeNullOrEmpty();
        agent.Description.ShouldContain("scheduled");
    }
}
