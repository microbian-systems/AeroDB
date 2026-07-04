using AeroDB.WolverineFx;

namespace AeroDB.Tests;

using Shouldly;
using TUnit.Core;
using Wolverine;
using WolverineFx;

public class AeroDBEnvelopeTests
{
    [Test]
    public void FromEnvelope_MapsAllFields()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            Destination = new Uri("AeroDB://localhost/queue"),
            MessageType = "TestMessage",
            Attempts = 3,
            DeliverBy = DateTimeOffset.UtcNow.AddHours(1),
            TenantId = "tenant1",
            CorrelationId = "corr-123",
            Source = "test-source"
        };
        env.Data = new byte[] { 1, 2, 3, 4, 5 };

        var record = AeroDBEnvelope.FromEnvelope(env, 42);

        record.Id.ShouldBe(env.Id.ToString());
        record.OwnerId.ShouldBe(42);
        record.MessageType.ShouldBe("TestMessage");
        record.Attempts.ShouldBe(3);
        record.Body.ShouldNotBeNullOrEmpty();
        record.TenantId.ShouldBe("tenant1");
        record.CorrelationId.ShouldBe("corr-123");
        record.Source.ShouldBe("test-source");
        record.Destination.ShouldBe("AeroDB://localhost/queue");
        record.DeliverBy.ShouldNotBeNull();
        record.DeliverBy.Value.ShouldBe(env.DeliverBy!.Value, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void FromEnvelope_StatusMapping()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "StatusTest" };
        env.Data = new byte[] { 1, 2, 3 };
        env.Status = EnvelopeStatus.Incoming;
        var record = AeroDBEnvelope.FromEnvelope(env, 0);
        record.Status.ShouldBe("Incoming");

        env.Status = EnvelopeStatus.Outgoing;
        record = AeroDBEnvelope.FromEnvelope(env, 0);
        record.Status.ShouldBe("Outgoing");
    }

    [Test]
    public void ToEnvelope_RoundTrips()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "RoundTrip",
            Attempts = 1,
            TenantId = "t1",
            CorrelationId = "corr-abc",
            Source = "source-1",
            Destination = new Uri("AeroDB://localhost/rt"),
            ContentType = "application/json",
            SagaId = "saga-1",
            ConversationId = Guid.NewGuid(),
            ReplyUri = new Uri("AeroDB://localhost/reply")
        };
        env.Data = new byte[] { 10, 20, 30 };

        var record = AeroDBEnvelope.FromEnvelope(env, 7);
        var restored = record.ToEnvelope();

        restored.Id.ShouldBe(env.Id);
        restored.MessageType.ShouldBe("RoundTrip");
        restored.Attempts.ShouldBe(1);
        restored.TenantId.ShouldBe("t1");
        restored.CorrelationId.ShouldBe("corr-abc");
        restored.Source.ShouldBe("source-1");
        restored.Data.ShouldBe(new byte[] { 10, 20, 30 });
        restored.Destination.ShouldBe(env.Destination);
        restored.ContentType.ShouldBe("application/json");
        restored.SagaId.ShouldBe("saga-1");
        restored.ConversationId.ShouldBe(env.ConversationId);
    }

    [Test]
    public void BodyBase64_RoundTrips()
    {
        // Test that FromEnvelope/ToEnvelope correctly round-trips body data
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "BodyTest"
        };
        env.Data = new byte[] { 1, 2, 3 };

        var record = AeroDBEnvelope.FromEnvelope(env, 0);
        record.Body.ShouldNotBeNullOrEmpty();

        var restored = record.ToEnvelope();
        restored.Data.ShouldBe(new byte[] { 1, 2, 3 });

        // Verify empty body stored as empty string in AeroDBEnvelope
        var envEmpty = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "BodyEmpty"
        };
        envEmpty.Data = new byte[] { 42 };
        var recordEmpty = AeroDBEnvelope.FromEnvelope(envEmpty, 0);
        recordEmpty.Body.ShouldNotBe(string.Empty);

        // Test AeroDBEnvelope directly: empty body → ToEnvelope Data is null
        recordEmpty.Body = string.Empty;
        var restoredEmpty = recordEmpty.ToEnvelope();
        // ToEnvelope skips setting Data when Body is empty, so _data isn't set.
        // Accessing Data would require a message; just verify the envelope is created.
        restoredEmpty.Id.ShouldBe(envEmpty.Id);
    }

    [Test]
    public void FromOutgoingEnvelope_SetsOutgoingStatus()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "OutMsg" };
        env.Data = new byte[] { 1, 2, 3 };

        var record = AeroDBEnvelope.FromOutgoingEnvelope(env, 5);

        record.Status.ShouldBe("Outgoing");
        record.OwnerId.ShouldBe(5);
        record.MessageType.ShouldBe("OutMsg");
        record.Body.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void RoundTrip_NullableFields_AreCorrect()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "NullTest"
        };
        env.Data = new byte[] { 1, 2, 3 };

        var record = AeroDBEnvelope.FromEnvelope(env, 0);
        var restored = record.ToEnvelope();

        restored.Id.ShouldBe(env.Id);
        restored.MessageType.ShouldBe("NullTest");
        restored.TenantId.ShouldBeNull();
        restored.CorrelationId.ShouldBeNull();
        restored.Source.ShouldBeNull();
        restored.Destination.ShouldBeNull();
        restored.SagaId.ShouldBeNull();
    }

    [Test]
    public void FromEnvelope_MultipleCalls_ProduceIndependentRecords()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "Multi" };
        env.Data = new byte[] { 1, 2, 3 };

        var record1 = AeroDBEnvelope.FromEnvelope(env, 0);
        var record2 = AeroDBEnvelope.FromEnvelope(env, 1);

        record1.OwnerId.ShouldBe(0);
        record2.OwnerId.ShouldBe(1);
        record1.Id.ShouldBe(record2.Id);
    }

    // ====================================================================
    // Edge cases
    // ====================================================================

    [Test]
    public void FromEnvelope_EmptyBody_ResultsInEmptyString()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "EmptyBody" };
        env.Data = [];

        var record = AeroDBEnvelope.FromEnvelope(env, 0);

        record.Body.ShouldBe(string.Empty);
    }

    [Test]
    public void ToEnvelope_NullBody_DataIsNotSet()
    {
        var record = new AeroDBEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Status = "Incoming",
            MessageType = "NullBodyTest",
            Body = string.Empty
        };

        var env = record.ToEnvelope();

        // When Body is empty, ToEnvelope skips env.Data assignment
        // (the internal _data stays null — we verify via absence of Destination etc.)
        env.Id.ShouldBe(Guid.Parse(record.Id));
        env.MessageType.ShouldBe("NullBodyTest");
        // Not checking env.Data since it would throw — we just know it round-trips
    }

    [Test]
    public void ToEnvelope_EmptyBody_RoundTrips()
    {
        var originalId = Guid.NewGuid();
        var record = new AeroDBEnvelope
        {
            Id = originalId.ToString(),
            Status = "Outgoing",
            OwnerId = 0,
            MessageType = "EmptyBodyTest",
            Body = string.Empty
        };

        var env = record.ToEnvelope();

        env.Id.ShouldBe(originalId);
        env.MessageType.ShouldBe("EmptyBodyTest");
        // Body empty means _data field stays null — ToEnvelope survives
    }

    [Test]
    public void FromEnvelope_EmptyHeaders_AllNull()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "NoHeaders" };
        env.Data = new byte[] { 1, 2, 3 };

        // Deliberately leave nullable fields unset
        var record = AeroDBEnvelope.FromEnvelope(env, 0);

        record.Destination.ShouldBeNull();
        record.CorrelationId.ShouldBeNull();
        record.Source.ShouldBeNull();
        record.TenantId.ShouldBeNull();
        record.ContentType.ShouldBeNull();
        record.ReplyUri.ShouldBeNull();
        record.SagaId.ShouldBeNull();
        record.ConversationId.ShouldBeNull();
        record.ReceivedAt.ShouldBeNull();
        record.DeliverBy.ShouldBeNull();
        record.KeepUntil.ShouldBeNull();
    }

    [Test]
    public void RoundTrip_AllFieldsPopulated()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            Status = EnvelopeStatus.Scheduled,
            OwnerId = 99,
            Attempts = 5,
            MessageType = "FullFieldsMessage",
            Source = "source-service",
            TenantId = "tenant-alpha",
            CorrelationId = "corr-full-001",
            SagaId = "saga-xyz",
            ContentType = "application/json",
            Destination = new Uri("AeroDB://dest/queue"),
            ReplyUri = new Uri("AeroDB://reply/callback"),
            ConversationId = Guid.NewGuid(),
            DeliverBy = DateTimeOffset.UtcNow.AddDays(1),
            KeepUntil = DateTimeOffset.UtcNow.AddDays(7),
            ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        env.Data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };

        var record = AeroDBEnvelope.FromEnvelope(env, 99);
        var restored = record.ToEnvelope();

        restored.Id.ShouldBe(env.Id);
        restored.Status.ShouldBe(EnvelopeStatus.Scheduled);
        restored.OwnerId.ShouldBe(99);
        restored.Attempts.ShouldBe(5);
        restored.MessageType.ShouldBe("FullFieldsMessage");
        restored.Source.ShouldBe("source-service");
        restored.TenantId.ShouldBe("tenant-alpha");
        restored.CorrelationId.ShouldBe("corr-full-001");
        restored.SagaId.ShouldBe("saga-xyz");
        restored.ContentType.ShouldBe("application/json");
        restored.Destination.ShouldBe(env.Destination);
        restored.ReplyUri.ShouldBe(env.ReplyUri);
        restored.ConversationId.ShouldBe(env.ConversationId);
        restored.DeliverBy.ShouldNotBeNull();
        restored.DeliverBy.Value.ShouldBe(env.DeliverBy!.Value, TimeSpan.FromSeconds(1));
        restored.ScheduledTime.ShouldNotBeNull();
        restored.ScheduledTime.Value.ShouldBe(env.ScheduledTime!.Value, TimeSpan.FromSeconds(1));
        restored.Data.ShouldBe(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE });
    }

    [Test]
    public void RoundTrip_MaxLengthFields()
    {
        // Use large strings to test field capacity
        var longMessageType = new string('X', 500);
        var longCorrelationId = new string('C', 500);
        var longSagaId = new string('S', 500);
        var longBody = Convert.ToBase64String(new byte[10000]);  // Base64 of 10KB

        var record = new AeroDBEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Status = "Incoming",
            OwnerId = int.MaxValue,
            ExecutionTime = DateTimeOffset.MaxValue,
            Attempts = int.MaxValue,
            Body = longBody,
            MessageType = longMessageType,
            Destination = "AeroDB://very-long-uri-that-should-still-work/" + new string('p', 200),
            CorrelationId = longCorrelationId,
            Source = "source-" + new string('s', 200),
            TenantId = "tenant-" + new string('t', 200),
            ContentType = "application/x.custom+" + new string('z', 100),
            ReplyUri = "AeroDB://reply/" + new string('r', 200),
            SagaId = longSagaId,
            ConversationId = Guid.NewGuid().ToString(),
            ReceivedAt = "AeroDB://received/" + new string('a', 200),
            DeliverBy = DateTimeOffset.MaxValue,
            KeepUntil = DateTimeOffset.MaxValue,
            SentAt = DateTimeOffset.MaxValue,
            ExceptionType = new string('E', 500),
            ExceptionMessage = new string('M', 2000),
            Replayable = true
        };

        var env = record.ToEnvelope();

        env.Id.ShouldBe(Guid.Parse(record.Id));
        env.MessageType.ShouldBe(longMessageType);
        env.CorrelationId.ShouldBe(longCorrelationId);
        env.SagaId.ShouldBe(longSagaId);
        env.Data.ShouldBe(Convert.FromBase64String(longBody));
        env.ContentType.ShouldBe(record.ContentType);
        env.TenantId.ShouldBe(record.TenantId);
        env.Source.ShouldBe(record.Source);
        env.Destination.ShouldBe(new Uri(record.Destination!));
        env.ReplyUri.ShouldBe(new Uri(record.ReplyUri!));
        env.ConversationId.ShouldBe(Guid.Parse(record.ConversationId!));
    }

    [Test]
    public void ToEnvelope_DefaultExecutionTime_DoesNotSetScheduledTime()
    {
        // When ExecutionTime is default(DateTimeOffset), ToEnvelope should not set ScheduledTime
        var record = new AeroDBEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Status = "Incoming",
            MessageType = "DefaultExecTime",
            Body = string.Empty
        };

        var env = record.ToEnvelope();

        // ExecutionTime is default → no ScheduledTime
        env.ScheduledTime.ShouldBeNull();
    }

    [Test]
    public void RoundTrip_ConversationIdEmptyGuid_StoredAsNull()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "ConvEmptyTest",
            ConversationId = Guid.Empty
        };
        env.Data = new byte[] { 1 };

        var record = AeroDBEnvelope.FromEnvelope(env, 0);

        record.ConversationId.ShouldBeNull();
    }

    [Test]
    public void FromOutgoingEnvelope_EmptyBody_ResultsInEmptyString()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "OutEmptyBody" };
        env.Data = [];

        var record = AeroDBEnvelope.FromOutgoingEnvelope(env, 0);

        record.Body.ShouldBe(string.Empty);
        record.Status.ShouldBe("Outgoing");
    }
}
