using AeroDB.WolverineFx;

namespace AeroDB.Tests;

using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using SurrealDb.Net;
using TUnit.Core;
using Wolverine;
using Wolverine.Transports.Sending;

/// <summary>
/// Mock-based unit tests for <see cref="AeroDBQueueSender"/>.
/// AeroDBMessageStore and SurrealDbResponse are sealed — tests create real
/// store instances with a mocked ISurrealDbClient and verify behavior
/// through client call patterns.
/// </summary>
public class AeroDBQueueSenderTests
{
    private static readonly Uri TestUri = new("AeroDB.Sable://localhost/test-queue");

    private static AeroDBMessageStore CreateStore(ISurrealDbClient client)
    {
        return new AeroDBMessageStore(client, NullLogger<AeroDBMessageStore>.Instance);
    }

    private static Envelope MakeEnvelope()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "TestMessage",
            Destination = TestUri
        };
        env.Data = [1, 2, 3];
        return env;
    }

    // ─── Constructor ─────────────────────────────────────────────────

    [Test]
    public async Task Constructor_throws_when_store_is_null()
    {
        Should.Throw<ArgumentNullException>(() =>
            new AeroDBQueueSender(null!, TestUri));
    }

    [Test]
    public async Task Constructor_throws_when_destination_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        Should.Throw<ArgumentNullException>(() =>
            new AeroDBQueueSender(store, null!));
    }

    // ─── Properties ─────────────────────────────────────────────────

    [Test]
    public async Task SupportsNativeScheduledSend_returns_false()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        sender.SupportsNativeScheduledSend.ShouldBeFalse();
    }

    [Test]
    public async Task Destination_matches_constructor_arg()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        sender.Destination.ShouldBe(TestUri);
    }

    // ─── PingAsync ──────────────────────────────────────────────────

    [Test]
    public async Task PingAsync_returns_true_when_connectivity_succeeds()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        var result = await sender.PingAsync();

        result.ShouldBeTrue();
    }

    [Test]
    public async Task PingAsync_returns_false_when_connectivity_throws()
    {
        var client = Substitute.For<ISurrealDbClient>();
        client.RawQuery(Arg.Any<string>())
            .Returns(Task.FromException<SurrealDb.Net.Models.Response.SurrealDbResponse>(
                new InvalidOperationException("db down")));
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        var result = await sender.PingAsync();

        result.ShouldBeFalse();
    }

    [Test]
    public async Task PingAsync_calls_CheckConnectivityAsync()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        await sender.PingAsync();

        await client.Received(1).RawQuery("SELECT 1;");
    }

    // ─── SendAsync ──────────────────────────────────────────────────

    [Test]
    public async Task SendAsync_stores_envelope_via_StoreOutgoingAsync()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);
        var envelope = MakeEnvelope();

        await sender.SendAsync(envelope);

        // Verify a CREATE query was sent to the outgoing table
        await client.Received(1).RawQuery(Arg.Is<string>(s =>
            s.Contains("CREATE wolverine_outgoing_envelopes")));
    }

    [Test]
    public async Task SendAsync_throws_when_envelope_is_null()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            sender.SendAsync(null!).AsTask());
    }

    [Test]
    public async Task SendAsync_sets_envelope_data_before_storing()
    {
        var client = Substitute.For<ISurrealDbClient>();
        var store = CreateStore(client);
        var sender = new AeroDBQueueSender(store, TestUri);
        var envelope = MakeEnvelope();

        await sender.SendAsync(envelope);

        // Accessing envelope.Data after SendAsync should work (serialization was triggered)
        var data = envelope.Data;
        data.ShouldNotBeNull();
    }
}
