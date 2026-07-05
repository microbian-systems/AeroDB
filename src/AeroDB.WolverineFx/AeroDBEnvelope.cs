using SurrealDb.Net.Models;
using Wolverine;

namespace AeroDB.WolverineFx;

/// <summary>
/// SurrealDB record type for Wolverine message envelopes.
/// Maps to wolverine_incoming_envelopes and wolverine_outgoing_envelopes tables.
/// The body is stored as Base64 string (SurrealDB CBOR byte[] round-trip may be unreliable).
/// </summary>
public sealed class AeroDBEnvelope : Record
{
    /// <summary>Wolverine envelope ID (Guid as string).</summary>
    public new string Id { get; set; } = string.Empty;

    /// <summary>Envelope status: Incoming, Outgoing, Handled, Scheduled, etc.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Owner node ID (int).</summary>
    public int OwnerId { get; set; }

    /// <summary>Execution time as DateTimeOffset.</summary>
    public DateTimeOffset ExecutionTime { get; set; }

    /// <summary>Number of delivery attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Serialized message body (Base64-encoded bytes).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Message type name.</summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>Destination URI.</summary>
    public string? Destination { get; set; }

    /// <summary>Deliver-by deadline.</summary>
    public DateTimeOffset? DeliverBy { get; set; }

    /// <summary>Correlation ID.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Source URI.</summary>
    public string? Source { get; set; }

    /// <summary>Tenant ID (for multi-tenancy).</summary>
    public string? TenantId { get; set; }

    /// <summary>Keep-until expiry.</summary>
    public DateTimeOffset? KeepUntil { get; set; }

    /// <summary>Message content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>The received-at URI (listener address).</summary>
    public string? ReceivedAt { get; set; }

    /// <summary>Reply URI for request/reply.</summary>
    public string? ReplyUri { get; set; }

    /// <summary>Saga ID if part of a saga.</summary>
    public string? SagaId { get; set; }

    /// <summary>Conversation ID for correlation.</summary>
    public string? ConversationId { get; set; }

    // Dead letter specific fields
    /// <summary>Exception type name (dead letters only).</summary>
    public string? ExceptionType { get; set; }

    /// <summary>Exception message (dead letters only).</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>Sent at timestamp.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Whether this dead letter is replayable.</summary>
    public bool? Replayable { get; set; }

    /// <summary>Map from Wolverine Envelope to AeroDBEnvelope (for incoming).</summary>
    public static AeroDBEnvelope FromEnvelope(Envelope env, int ownerId)
    {
        var body = env.Data is { Length: > 0 } ? Convert.ToBase64String(env.Data) : string.Empty;
        return new AeroDBEnvelope
        {
            Id = env.Id.ToString(),
            Status = env.Status.ToString(),
            OwnerId = ownerId,
            ExecutionTime = env.ScheduledTime ?? DateTimeOffset.UtcNow,
            Attempts = env.Attempts,
            Body = body,
            MessageType = env.MessageType ?? string.Empty,
            Destination = env.Destination?.ToString(),
            DeliverBy = env.DeliverBy,
            CorrelationId = env.CorrelationId,
            Source = env.Source,
            TenantId = env.TenantId,
            KeepUntil = env.KeepUntil,
            ContentType = env.ContentType,
            ReplyUri = env.ReplyUri?.ToString(),
            SagaId = env.SagaId,
            ConversationId = env.ConversationId == Guid.Empty ? null : env.ConversationId.ToString()
        };
    }

    /// <summary>Map from Wolverine Envelope to AeroDBEnvelope (for outgoing).</summary>
    public static AeroDBEnvelope FromOutgoingEnvelope(Envelope env, int ownerId)
    {
        var body = env.Data is { Length: > 0 } ? Convert.ToBase64String(env.Data) : string.Empty;
        return new AeroDBEnvelope
        {
            Id = env.Id.ToString(),
            Status = EnvelopeStatus.Outgoing.ToString(),
            OwnerId = ownerId,
            ExecutionTime = env.ScheduledTime ?? DateTimeOffset.UtcNow,
            Attempts = env.Attempts,
            Body = body,
            MessageType = env.MessageType ?? string.Empty,
            Destination = env.Destination?.ToString(),
            DeliverBy = env.DeliverBy,
            CorrelationId = env.CorrelationId,
            Source = env.Source,
            TenantId = env.TenantId,
            KeepUntil = env.KeepUntil,
            ContentType = env.ContentType,
            ReplyUri = env.ReplyUri?.ToString(),
            SagaId = env.SagaId,
            ConversationId = env.ConversationId == Guid.Empty ? null : env.ConversationId.ToString()
        };
    }

    /// <summary>Convert back to Wolverine Envelope.</summary>
    public Envelope ToEnvelope()
    {
        var env = new Envelope
        {
            Id = Guid.Parse(Id),
            Status = Enum.TryParse<EnvelopeStatus>(Status, out var s) ? s : EnvelopeStatus.Incoming,
            OwnerId = OwnerId,
            Attempts = Attempts,
            MessageType = MessageType,
            Source = Source,
            TenantId = TenantId,
            CorrelationId = CorrelationId,
            SagaId = SagaId,
            ContentType = ContentType
        };

        if (ExecutionTime != default)
            env.ScheduledTime = ExecutionTime;

        if (!string.IsNullOrEmpty(Destination))
            env.Destination = new Uri(Destination);

        if (DeliverBy.HasValue)
            env.DeliverBy = DeliverBy;

        if (KeepUntil.HasValue)
            env.KeepUntil = KeepUntil;

        if (!string.IsNullOrEmpty(ReplyUri))
            env.ReplyUri = new Uri(ReplyUri);

        if (ConversationId is { Length: > 0 })
            env.ConversationId = Guid.Parse(ConversationId);

        if (!string.IsNullOrEmpty(Body))
            env.Data = Convert.FromBase64String(Body);

        return env;
    }
}
