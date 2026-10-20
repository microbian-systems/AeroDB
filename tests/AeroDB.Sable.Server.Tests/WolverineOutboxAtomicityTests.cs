using System.Diagnostics;
using AeroDB.WolverineFx;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net.Models.Response;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class WolverineOutboxAtomicityTests
{
    [Test]
    public async Task AuthenticatedNodeAuthorityRoundTripsPersistedNodeIdentity(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messages = await CreateMessageStoreAsync(store);
        var node = new Wolverine.Runtime.Agents.WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 88,
            Description = "authority-roundtrip",
            ControlUri = new Uri("loopback://authority-control"),
            Version = new Version(6, 17, 1),
            Started = new DateTimeOffset(2026, 10, 11, 12, 30, 0, TimeSpan.Zero),
            LastHealthCheck = new DateTimeOffset(2026, 10, 11, 12, 31, 0, TimeSpan.Zero)
        };
        node.Capabilities.Add(new Uri("loopback://authority-capability"));
        node.ActiveAgents.Add(new Uri("loopback://authority-agent"));

        await messages.PersistAsync(node, ct);

        var loaded = await messages.LoadAllNodesAsync(ct);
        loaded.Count.ShouldBe(1);
        var restored = loaded.Single();
        restored.NodeId.ShouldBe(node.NodeId);
        restored.AssignedNodeNumber.ShouldBe(88);
        restored.Description.ShouldBe(node.Description);
        restored.ControlUri.ShouldBe(node.ControlUri);
        restored.Version.ShouldBe(node.Version);
        restored.Started.ShouldBe(node.Started);
        restored.LastHealthCheck.ShouldBe(node.LastHealthCheck);
        restored.Capabilities.ShouldBe([new Uri("loopback://authority-capability")]);
        restored.ActiveAgents.ShouldBe([new Uri("loopback://authority-agent")]);
    }

    [Test]
    public async Task AuthenticatedNodeAuthorityRejectsMixedValidAndMalformedRowsWithoutReturningAPartialAuthoritySet(
        CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messages = await CreateMessageStoreAsync(store);
        var valid = new Wolverine.Runtime.Agents.WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 77,
            Description = "active-authority",
            Started = new DateTimeOffset(2026, 10, 11, 12, 30, 0, TimeSpan.Zero),
            LastHealthCheck = new DateTimeOffset(2026, 10, 11, 12, 31, 0, TimeSpan.Zero)
        };

        await messages.PersistAsync(valid, ct);
        var malformed = await store.Client.RawQuery(
            $"CREATE wolverine_nodes:`{Guid.NewGuid():D}` CONTENT {{ node_number: 0, description: 'malformed-authority' }};",
            cancellationToken: ct);
        malformed.EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(() => messages.LoadAllNodesAsync(ct));
    }

    [Test]
    public async Task TenantStoresRemainPhysicallyIsolatedAfterAnAuthenticatedRestart(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        var alpha = new AeroDBTenantStoreLocation("alpha", "local", "ca_alpha", "main");
        var beta = new AeroDBTenantStoreLocation("beta", "local", "ca_beta", "main");
        var alphaListener = new Uri("loopback://alpha-recovery");
        var betaListener = new Uri("loopback://beta-recovery");

        await using (var alphaStore = await fixture.CreateStoreAsync(alpha.Namespace, alpha.Database, ct))
        await using (var betaStore = await fixture.CreateStoreAsync(beta.Namespace, beta.Database, ct))
        {
            var alphaMessages = await CreateMessageStoreAsync(alphaStore);
            var betaMessages = await CreateMessageStoreAsync(betaStore);
            var alphaStale = CreateIncomingEnvelope(alphaListener);
            var alphaActive = CreateIncomingEnvelope(alphaListener);
            var betaStale = CreateIncomingEnvelope(betaListener);
            var betaActive = CreateIncomingEnvelope(betaListener);

            await alphaMessages.StoreIncomingAsync([alphaStale, alphaActive]);
            await betaMessages.StoreIncomingAsync([betaStale, betaActive]);
            await alphaMessages.ReassignIncomingAsync(41, [alphaStale]);
            await alphaMessages.ReassignIncomingAsync(77, [alphaActive]);
            await betaMessages.ReassignIncomingAsync(41, [betaStale]);
            await betaMessages.ReassignIncomingAsync(77, [betaActive]);
            await alphaMessages.DisposeAsync();
            await betaMessages.DisposeAsync();
        }

        await using var restartedAlphaStore = await fixture.CreateStoreAsync(alpha.Namespace, alpha.Database, ct);
        await using var restartedBetaStore = await fixture.CreateStoreAsync(beta.Namespace, beta.Database, ct);
        var restartedAlphaMessages = await CreateMessageStoreAsync(restartedAlphaStore);
        var restartedBetaMessages = await CreateMessageStoreAsync(restartedBetaStore);

        (await restartedAlphaMessages.AllIncomingAsync()).Select(x => x.Destination).ShouldAllBe(destination => destination == alphaListener);
        (await restartedBetaMessages.AllIncomingAsync()).Select(x => x.Destination).ShouldAllBe(destination => destination == betaListener);
    }

    [Test]
    public async Task AuthenticatedDocumentStoreFactoryInitializesSchemaAndPersistsOutgoingEnvelope(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        (await fixture.IsAnonymousSqlDeniedAsync(ct)).ShouldBeTrue();
        var messageStore = await CreateMessageStoreAsync(store);
        var envelope = CreateEnvelope();
        (await messageStore.LoadOutgoingAsync(new Uri("loopback://no-messages"))).ShouldBeEmpty();

        await using var session = await store.LightweightSessionAsync(ct);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 0);
        await transaction.PersistOutgoingAsync(envelope);
        session.QueueSqlCommand("{database}", "CREATE type::record(\"outbox_business\", $p0) CONTENT { committed: true };", "commit-proof");

        (await messageStore.AllOutgoingAsync()).ShouldBeEmpty();
        await session.SaveChangesAsync(ct);
        transaction.MarkCommitted();

        envelope.WasPersistedInOutbox.ShouldBeTrue();
        await using var readerStore = await fixture.CreateStoreAsync(ct);
        var recovered = await (await CreateMessageStoreAsync(readerStore)).LoadOutgoingAsync(envelope.Destination!);
        recovered.Count.ShouldBe(1);
        recovered[0].Id.ShouldBe(envelope.Id);
        recovered[0].Data.ShouldBe(envelope.Data);
        (await CountAsync(store, "outbox_business", ct)).ShouldBe(1);
    }

    [Test]
    public async Task ExplicitRollbackLeavesBusinessAndQueuedOutgoingInvisible(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var envelope = CreateEnvelope();

        await using var session = await store.LightweightSessionAsync(ct);
        await using (var databaseTransaction = await session.BeginTransactionAsync(ct))
        {
            var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
            await transaction.PersistOutgoingAsync(envelope);
            session.QueueSqlCommand("{database}", "CREATE type::record(\"outbox_business\", $p0) CONTENT { committed: true };", "rollback-proof");
            await session.SaveChangesAsync(ct);
            await databaseTransaction.RollbackAsync(ct);
        }

        (await messageStore.AllOutgoingAsync()).ShouldBeEmpty();
        (await CountAsync(store, "outbox_business", ct)).ShouldBe(0);
        envelope.WasPersistedInOutbox.ShouldBeFalse();
    }

    [Test]
    public async Task IncomingHandledAndBusinessWriteCommitTogether(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var envelope = CreateEnvelope();

        await using var session = await store.LightweightSessionAsync(ct);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
        await transaction.PersistIncomingAsync(envelope);
        transaction.QueueHandlingForPreviouslyPersistedIncoming(envelope);
        session.QueueSqlCommand("{database}", "CREATE type::record(\"inbox_business\", $p0) CONTENT { committed: true };", "incoming-proof");

        await session.SaveChangesAsync(ct);
        transaction.MarkCommitted();

        envelope.WasPersistedInInbox.ShouldBeTrue();
        envelope.Status.ShouldBe(EnvelopeStatus.Handled);
        (await CountAsync(store, "inbox_business", ct)).ShouldBe(1);
        var incoming = await messageStore.AllIncomingAsync();
        incoming.Count.ShouldBe(1);
        incoming[0].Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task GloballyOwnedIncomingRecoveryReturnsOnlyTheRequestedListenerAddress(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var firstAddress = new Uri("loopback://listener-one");
        var secondAddress = new Uri("loopback://listener-two");
        var first = CreateEnvelope();
        var second = CreateEnvelope();
        first.Status = EnvelopeStatus.Incoming;
        second.Status = EnvelopeStatus.Incoming;
        first.Destination = firstAddress;
        second.Destination = secondAddress;

        await messageStore.StoreIncomingAsync(first);
        await messageStore.StoreIncomingAsync(second);

        var recovered = await messageStore.LoadPageOfGloballyOwnedIncomingAsync(firstAddress, 10);
        var secondRecovered = await messageStore.LoadPageOfGloballyOwnedIncomingAsync(secondAddress, 10);
        var noMessages = await messageStore.LoadPageOfGloballyOwnedIncomingAsync(
            new Uri("loopback://listener-without-messages"),
            10);

        recovered.Count.ShouldBe(1);
        recovered[0].Id.ShouldBe(first.Id);
        recovered[0].Data.ShouldBe(first.Data);
        secondRecovered.Count.ShouldBe(1);
        secondRecovered[0].Id.ShouldBe(second.Id);
        secondRecovered[0].Data.ShouldBe(second.Data);
        noMessages.ShouldBeEmpty();
    }

    [Test]
    public async Task OutgoingRecoveryFailsForACorruptPersistedBody(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var envelope = CreateEnvelope();

        await messageStore.StoreOutgoingAsync(envelope, 0);
        var response = await store.Client.RawQuery(
            "UPDATE type::record(\"wolverine_outgoing_envelopes\", $p0) SET body = $p1;",
            new Dictionary<string, object?>
            {
                ["p0"] = envelope.Id.ToString(),
                ["p1"] = "not-base64"
            },
            ct);
        response.EnsureAllOks();

        await Should.ThrowAsync<FormatException>(() => messageStore.LoadOutgoingAsync(envelope.Destination!));
    }

    [Test]
    public async Task IncomingRecoveryFailsForAnUndefinedPersistedStatus(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var listener = new Uri("loopback://invalid-status-listener");
        var envelope = CreateEnvelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = listener;

        await messageStore.StoreIncomingAsync(envelope);
        var response = await store.Client.RawQuery(
            "UPDATE type::record(\"wolverine_incoming_envelopes\", $p0) SET status = $p1;",
            new Dictionary<string, object?>
            {
                ["p0"] = envelope.Id.ToString(),
                ["p1"] = "999"
            },
            ct);
        response.EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(
            () => messageStore.AllIncomingAsync());
    }

    [Test]
    public async Task IncomingRecoveryFailsForABlankMessageTypeAndNonGuidRecordId(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var listener = new Uri("loopback://invalid-message-listener");
        var blankMessageType = CreateEnvelope();
        blankMessageType.Status = EnvelopeStatus.Incoming;
        blankMessageType.Destination = listener;

        await messageStore.StoreIncomingAsync(blankMessageType);
        var blankResponse = await store.Client.RawQuery(
            "UPDATE type::record(\"wolverine_incoming_envelopes\", $p0) SET message_type = $p1;",
            new Dictionary<string, object?>
            {
                ["p0"] = blankMessageType.Id.ToString(),
                ["p1"] = " "
            },
            ct);
        blankResponse.EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(() => messageStore.AllIncomingAsync());

        var deleteBlankResponse = await store.Client.RawQuery(
            "DELETE type::record(\"wolverine_incoming_envelopes\", $p0);",
            new Dictionary<string, object?> { ["p0"] = blankMessageType.Id.ToString() },
            ct);
        deleteBlankResponse.EnsureAllOks();
        (await messageStore.AllIncomingAsync()).ShouldBeEmpty();

        var malformedIdResponse = await store.Client.RawQuery(
            "CREATE type::record(\"wolverine_incoming_envelopes\", $p0) CONTENT $p1;",
            new Dictionary<string, object?>
            {
                ["p0"] = "not-a-guid",
                ["p1"] = new Dictionary<string, object?>
                {
                    ["status"] = "Incoming",
                    ["owner_id"] = 0,
                    ["attempts"] = 0,
                    ["execution_time"] = DateTimeOffset.UtcNow,
                    ["body"] = string.Empty,
                    ["message_type"] = "valid.message",
                    ["received_at"] = listener.ToString()
                }
            },
            ct);
        malformedIdResponse.EnsureAllOks();

        await Should.ThrowAsync<InvalidOperationException>(() => messageStore.AllIncomingAsync());
    }

    [Test]
    public async Task MarkIncomingHandledRequiresAnExistingRecordAndExistsDistinguishesMissingRecords(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var existing = CreateEnvelope();
        existing.Status = EnvelopeStatus.Incoming;

        await messageStore.StoreIncomingAsync(existing);
        (await messageStore.ExistsAsync(existing, ct)).ShouldBeTrue();

        var missing = CreateEnvelope();
        (await messageStore.ExistsAsync(missing, ct)).ShouldBeFalse();
        await Should.ThrowAsync<SurrealDb.Net.Exceptions.Response.ResponseUnsuccessfulException>(
            () => messageStore.MarkIncomingEnvelopeAsHandledAsync(missing));

        await messageStore.MarkIncomingEnvelopeAsHandledAsync(existing);
        var handled = (await messageStore.AllIncomingAsync()).Single();
        handled.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task DuplicateIncomingEnvelopeReportsOnlyTheExistingDuplicateAndPreservesTheOriginal(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var original = CreateEnvelope();
        original.Status = EnvelopeStatus.Incoming;
        original.Data = "original-incoming-body"u8.ToArray();
        var duplicate = CreateEnvelope(original.Id);
        duplicate.Status = EnvelopeStatus.Incoming;
        duplicate.Data = "redelivered-body-must-not-replace-original"u8.ToArray();

        await messageStore.StoreIncomingAsync(original);

        var exception = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => messageStore.StoreIncomingAsync(duplicate));

        exception.Duplicates.Count.ShouldBe(1);
        exception.Duplicates.Single().Id.ShouldBe(original.Id);
        var stored = (await messageStore.AllIncomingAsync()).Single();
        stored.Id.ShouldBe(original.Id);
        stored.Data.ShouldBe(original.Data);
        stored.Status.ShouldBe(EnvelopeStatus.Incoming);
    }

    [Test]
    public async Task IncomingBatchWithOneExistingDuplicateIsAtomicAndIdentifiesOnlyThatDuplicate(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var existing = CreateEnvelope();
        existing.Status = EnvelopeStatus.Incoming;
        var firstFresh = CreateEnvelope();
        firstFresh.Status = EnvelopeStatus.Incoming;
        var duplicate = CreateEnvelope(existing.Id);
        duplicate.Status = EnvelopeStatus.Incoming;
        var secondFresh = CreateEnvelope();
        secondFresh.Status = EnvelopeStatus.Incoming;

        await messageStore.StoreIncomingAsync(existing);

        var exception = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => messageStore.StoreIncomingAsync([firstFresh, duplicate, secondFresh]));

        exception.Duplicates.Count.ShouldBe(1);
        exception.Duplicates.Single().Id.ShouldBe(existing.Id);
        var stored = await messageStore.AllIncomingAsync();
        stored.Count.ShouldBe(1);
        stored.Single().Id.ShouldBe(existing.Id);
        stored.ShouldNotContain(envelope => envelope.Id == firstFresh.Id);
        stored.ShouldNotContain(envelope => envelope.Id == secondFresh.Id);
    }

    [Test]
    public async Task IncomingBatchRejectsRepeatedInputIdsWithoutAcknowledgingOrPersistingEitherEnvelope(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var firstOccurrence = CreateEnvelope();
        firstOccurrence.Status = EnvelopeStatus.Incoming;
        var repeatedOccurrence = CreateEnvelope(firstOccurrence.Id);
        repeatedOccurrence.Status = EnvelopeStatus.Incoming;

        await Should.ThrowAsync<ArgumentException>(
            () => messageStore.StoreIncomingAsync([firstOccurrence, repeatedOccurrence]));

        (await messageStore.AllIncomingAsync()).ShouldBeEmpty();
        firstOccurrence.WasPersistedInInbox.ShouldBeFalse();
        repeatedOccurrence.WasPersistedInInbox.ShouldBeFalse();
    }

    [Test]
    public async Task IncomingBatchRollsBackEarlierCreatesWhenALaterDatabaseEventFails(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var firstFresh = CreateEnvelope();
        firstFresh.Status = EnvelopeStatus.Incoming;
        var rejected = CreateEnvelope();
        rejected.Status = EnvelopeStatus.Incoming;
        rejected.MessageType = "tests.atomic-failure";
        const string eventName = "reject_atomic_incoming_batch";

        var defineEvent = await store.Client.RawQuery(
            $"DEFINE EVENT {eventName} ON TABLE wolverine_incoming_envelopes " +
            "WHEN $event = 'CREATE' AND $after.message_type = 'tests.atomic-failure' THEN { " +
            "THROW 'deterministic incoming batch failure'; };",
            cancellationToken: ct);
        defineEvent.EnsureAllOks();

        try
        {
            await Should.ThrowAsync<SurrealDb.Net.Exceptions.Response.ResponseUnsuccessfulException>(
                () => messageStore.StoreIncomingAsync([firstFresh, rejected]));

            (await messageStore.AllIncomingAsync()).ShouldBeEmpty();
            firstFresh.WasPersistedInInbox.ShouldBeFalse();
            rejected.WasPersistedInInbox.ShouldBeFalse();
        }
        finally
        {
            var removeEvent = await store.Client.RawQuery(
                $"REMOVE EVENT {eventName} ON TABLE wolverine_incoming_envelopes;",
                cancellationToken: ct);
            removeEvent.EnsureAllOks();
        }
    }

    [Test]
    public async Task BatchHandlingRollsBackWhenALaterIncomingRecordIsMissing(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var existing = CreateEnvelope();
        existing.Status = EnvelopeStatus.Incoming;
        await messageStore.StoreIncomingAsync(existing);
        var missing = CreateEnvelope();

        await Should.ThrowAsync<SurrealDb.Net.Exceptions.Response.ResponseUnsuccessfulException>(
            () => messageStore.MarkIncomingEnvelopeAsHandledAsync([existing, missing]));

        var stillIncoming = (await messageStore.AllIncomingAsync()).Single();
        stillIncoming.Id.ShouldBe(existing.Id);
        stillIncoming.Status.ShouldBe(EnvelopeStatus.Incoming);
    }

    [Test]
    public async Task MissingIncomingHandleAbortsTheBusinessTransaction(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var missing = CreateEnvelope();
        missing.WasPersistedInInbox = true;

        await using var session = await store.LightweightSessionAsync(ct);
        var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 0);
        transaction.QueueHandlingForPreviouslyPersistedIncoming(missing);
        session.QueueSqlCommand(
            "{database}",
            "CREATE type::record(\"inbox_business\", $p0) CONTENT { committed: true };",
            "missing-handle-proof");

        await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(ct));
        (await CountAsync(store, "inbox_business", ct)).ShouldBe(0);
        missing.Status.ShouldNotBe(EnvelopeStatus.Handled);
    }

    [Test]
    public async Task AdapterRollbackClearsQueuedWorkBeforeTheSessionIsReused(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);

        await using var session = await store.LightweightSessionAsync(ct);
        var rolledBack = CreateEnvelope();
        await using var firstDatabaseTransaction = await session.BeginTransactionAsync(ct);
        var firstTransaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
        await firstTransaction.PersistOutgoingAsync(rolledBack);
        session.QueueSqlCommand(
            "{database}",
            "CREATE type::record(\"outbox_business\", $p0) CONTENT { committed: true };",
            "rollback-first");
        await session.SaveChangesAsync(ct);
        await firstTransaction.RollbackAsync();

        var committed = CreateEnvelope();
        var secondTransaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
        await using (var secondDatabaseTransaction = await session.BeginTransactionAsync(ct))
        {
            await secondTransaction.PersistOutgoingAsync(committed);
            session.QueueSqlCommand(
                "{database}",
                "CREATE type::record(\"outbox_business\", $p0) CONTENT { committed: true };",
                "rollback-second");
            await session.SaveChangesAsync(ct);
            await secondDatabaseTransaction.CommitAsync(ct);
        }
        secondTransaction.MarkCommitted();

        rolledBack.WasPersistedInOutbox.ShouldBeFalse();
        committed.WasPersistedInOutbox.ShouldBeTrue();
        var outgoing = await messageStore.AllOutgoingAsync();
        outgoing.Count.ShouldBe(1);
        outgoing.ShouldNotContain(envelope => envelope.Id == rolledBack.Id);
        outgoing.ShouldContain(envelope => envelope.Id == committed.Id);
        outgoing[0].Id.ShouldBe(committed.Id);
        (await OutboxBusinessExistsAsync(store, "rollback-first", ct)).ShouldBeFalse();
        (await OutboxBusinessExistsAsync(store, "rollback-second", ct)).ShouldBeTrue();
    }

    [Test]
    public async Task DuplicateOutgoingIdThrowsWithoutFalseSuccess(CancellationToken ct)
    {
        await using var fixture = await IsolatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var messageStore = await CreateMessageStoreAsync(store);
        var first = CreateEnvelope();

        await using (var session = await store.LightweightSessionAsync(ct))
        {
            var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
            await transaction.PersistOutgoingAsync(first);
            await session.SaveChangesAsync(ct);
            transaction.MarkCommitted();
        }

        var duplicate = CreateEnvelope(first.Id);
        await using (var session = await store.LightweightSessionAsync(ct))
        {
            var transaction = new AeroDBEnvelopeTransaction(messageStore, session, 41);
            await transaction.PersistOutgoingAsync(duplicate);
            session.QueueSqlCommand("{database}", "CREATE type::record(\"outbox_business\", $p0) CONTENT { committed: true };", "duplicate-proof");
            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(ct));
        }

        duplicate.WasPersistedInOutbox.ShouldBeFalse();
        (await messageStore.AllOutgoingAsync()).Count.ShouldBe(1);
        (await CountAsync(store, "outbox_business", ct)).ShouldBe(0);
    }

    private static async Task<AeroDBMessageStore> CreateMessageStoreAsync(
        IDocumentStore store,
        Uri? storeIdentity = null)
    {
        var messageStore = new AeroDBMessageStore(
            store.Client,
            NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null,
            storeOptions: store.Options,
            ownsClient: true,
            storeIdentity: storeIdentity);
        await messageStore.InitializeSchemaAsync();
        return messageStore;
    }

    private static Envelope CreateEnvelope(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Data = "atomic-outbox"u8.ToArray(),
        MessageType = "tests.atomic-outbox",
        Destination = new Uri("loopback://outbox"),
        Status = EnvelopeStatus.Outgoing
    };

    private static Envelope CreateIncomingEnvelope(Uri destination) => new()
    {
        Id = Guid.NewGuid(),
        Data = "tenant-recovery"u8.ToArray(),
        MessageType = "tests.tenant-recovery",
        Destination = destination,
        Status = EnvelopeStatus.Incoming
    };

    private static async Task<long> CountAsync(IDocumentStore store, string table, CancellationToken ct)
    {
        var response = await store.Client.RawQuery($"SELECT * FROM {table};", cancellationToken: ct);
        response.EnsureAllOks();
        return (response.GetValue<List<BusinessProof>>(0) ?? []).Count;
    }

    private static async Task<bool> OutboxBusinessExistsAsync(IDocumentStore store, string id, CancellationToken ct)
    {
        var response = await store.Client.RawQuery(
            "SELECT * FROM type::record(\"outbox_business\", $p0);",
            new Dictionary<string, object?> { ["p0"] = id },
            ct);
        response.EnsureAllOks();
        return (response.GetValue<List<BusinessProof>>(0) ?? []).Count > 0;
    }

    private sealed class BusinessProof
    {
        public bool Committed { get; set; }
    }

    private sealed class IsolatedSurrealFixture : IAsyncDisposable
    {
        private readonly string _containerName;
        private readonly int _port;
        private const string Username = "wolverine_atomicity_test";
        private const string Password = "wolverine-atomicity-local-only";

        private IsolatedSurrealFixture(string containerName, int port)
        {
            _containerName = containerName;
            _port = port;
        }

        public static async Task<IsolatedSurrealFixture> StartAsync(CancellationToken ct)
        {
            var fixture = new IsolatedSurrealFixture(
                $"aerodb-wolverine-proof-{Guid.NewGuid():N}",
                GetAvailableLoopbackPort());
            var arguments =
                $"run -d --rm --name {fixture._containerName} --label aerodb.testfixture=wolverine-outbox " +
                $"-p 127.0.0.1:{fixture._port}:8000 surrealdb/surrealdb:v3.2.4 " +
                $"start --user {Username} --pass {Password} memory";

            await RunDockerAsync(arguments, ct);

            try
            {
                await fixture.WaitUntilReadyAsync(ct);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<IDocumentStore> CreateStoreAsync(CancellationToken ct)
            => await CreateStoreAsync("wolverine_atomicity", "proof", ct);

        public async Task<IDocumentStore> CreateStoreAsync(string @namespace, string database, CancellationToken ct)
        {
            await EnsureNamespaceAndDatabaseAsync(@namespace, database, ct);
            var store = await CreateConnectedStoreAsync(@namespace, database, ct);
            try
            {
                await using var session = await store.LightweightSessionAsync(ct);
                await session.ExecuteSqlAsync(
                    "DEFINE TABLE IF NOT EXISTS outbox_business SCHEMALESS; DEFINE TABLE IF NOT EXISTS inbox_business SCHEMALESS;",
                    null,
                    ct);
                return store;
            }
            catch
            {
                await store.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunDockerAsync($"rm -f {_containerName}", timeout.Token, tolerateFailure: true);
        }

        private async Task WaitUntilReadyAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Exception? lastFailure = null;
            var stage = "connect";

            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    await using var store = await CreateConnectedStoreAsync(timeout.Token);
                    stage = "version";
                    var version = await store.GetVersionAsync(timeout.Token);
                    if (version?.Contains("3.2.4", StringComparison.Ordinal) == true)
                        return;

                    lastFailure = new InvalidOperationException("Unexpected server version.");
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }

                stage = "connect";
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"The isolated SurrealDB test fixture did not become ready during the {stage} stage. " +
                $"Last failure type: {lastFailure?.GetType().Name ?? "none"}.");
        }

        private Task<IDocumentStore> CreateConnectedStoreAsync(CancellationToken ct) =>
            CreateConnectedStoreAsync("wolverine_atomicity", "proof", ct);

        private async Task EnsureNamespaceAndDatabaseAsync(string @namespace, string database, CancellationToken ct)
        {
            await using var bootstrap = await CreateConnectedStoreAsync(ct);
            await using var session = await bootstrap.LightweightSessionAsync(ct);
            await session.ExecuteSqlAsync($"DEFINE NAMESPACE IF NOT EXISTS `{@namespace}`;", null, ct);
            await session.ExecuteSqlAsync($"DEFINE DATABASE IF NOT EXISTS `{database}`;", null, ct);
        }

        private async Task<IDocumentStore> CreateConnectedStoreAsync(string @namespace, string database, CancellationToken ct)
        {
            var store = Documents.For(options =>
            {
                options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
                options.Namespace = @namespace;
                options.Database = database;
                options.Username = Username;
                options.Password = Password;
            });

            try
            {
                await store.InitializeAsync(ct);
                return store;
            }
            catch
            {
                await store.DisposeAsync();
                throw;
            }
        }

        public async Task<bool> IsAnonymousSqlDeniedAsync(CancellationToken ct)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/sql")
            {
                Content = new StringContent("RETURN 1;")
            };
            using var response = await client.SendAsync(request, ct);
            return response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
        }

        private static int GetAvailableLoopbackPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task RunDockerAsync(string arguments, CancellationToken ct, bool tolerateFailure = false)
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start Docker for the isolated SurrealDB test fixture.");

            var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
            var standardError = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await standardOutput;
            var error = await standardError;
            if (!tolerateFailure && process.ExitCode != 0)
                throw new InvalidOperationException($"Docker command failed ({process.ExitCode}): {error}{output}");
        }
    }
}
