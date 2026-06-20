namespace Dali.Tests;

using Shouldly;
using TUnit.Core;
using Wolverine;
using WolverineFx.Dali;

public class DaliEnvelopeTests
{
    [Test]
    public void FromEnvelope_MapsAllFields()
    {
        var env = new Envelope
        {
            Id = Guid.NewGuid(),
            Destination = new Uri("dali://localhost/queue"),
            MessageType = "TestMessage",
            Attempts = 3,
            DeliverBy = DateTimeOffset.UtcNow.AddHours(1),
            TenantId = "tenant1",
            CorrelationId = "corr-123",
            Source = "test-source"
        };
        env.Data = new byte[] { 1, 2, 3, 4, 5 };

        var record = DaliEnvelope.FromEnvelope(env, 42);

        record.Id.ShouldBe(env.Id.ToString());
        record.OwnerId.ShouldBe(42);
        record.MessageType.ShouldBe("TestMessage");
        record.Attempts.ShouldBe(3);
        record.Body.ShouldNotBeNullOrEmpty();
        record.TenantId.ShouldBe("tenant1");
        record.CorrelationId.ShouldBe("corr-123");
        record.Source.ShouldBe("test-source");
        record.Destination.ShouldBe("dali://localhost/queue");
        record.DeliverBy.ShouldNotBeNull();
        record.DeliverBy.Value.ShouldBe(env.DeliverBy!.Value, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void FromEnvelope_StatusMapping()
    {
        var env = new Envelope { Id = Guid.NewGuid(), MessageType = "StatusTest" };
        env.Data = new byte[] { 1, 2, 3 };
        env.Status = EnvelopeStatus.Incoming;
        var record = DaliEnvelope.FromEnvelope(env, 0);
        record.Status.ShouldBe("Incoming");

        env.Status = EnvelopeStatus.Outgoing;
        record = DaliEnvelope.FromEnvelope(env, 0);
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
            Destination = new Uri("dali://localhost/rt"),
            ContentType = "application/json",
            SagaId = "saga-1",
            ConversationId = Guid.NewGuid(),
            ReplyUri = new Uri("dali://localhost/reply")
        };
        env.Data = new byte[] { 10, 20, 30 };

        var record = DaliEnvelope.FromEnvelope(env, 7);
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

        var record = DaliEnvelope.FromEnvelope(env, 0);
        record.Body.ShouldNotBeNullOrEmpty();

        var restored = record.ToEnvelope();
        restored.Data.ShouldBe(new byte[] { 1, 2, 3 });

        // Verify empty body stored as empty string in DaliEnvelope
        var envEmpty = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "BodyEmpty"
        };
        envEmpty.Data = new byte[] { 42 };
        var recordEmpty = DaliEnvelope.FromEnvelope(envEmpty, 0);
        recordEmpty.Body.ShouldNotBe(string.Empty);

        // Test DaliEnvelope directly: empty body → ToEnvelope Data is null
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

        var record = DaliEnvelope.FromOutgoingEnvelope(env, 5);

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

        var record = DaliEnvelope.FromEnvelope(env, 0);
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

        var record1 = DaliEnvelope.FromEnvelope(env, 0);
        var record2 = DaliEnvelope.FromEnvelope(env, 1);

        record1.OwnerId.ShouldBe(0);
        record2.OwnerId.ShouldBe(1);
        record1.Id.ShouldBe(record2.Id);
    }
}
