using System.Text.Json.Serialization;
using Dahomey.Cbor.Attributes;
using SurrealDb.Net.Json;
using SurrealDb.Net.Models;

namespace AeroDB.WolverineFx;

internal static class WolverineTableNames
{
    public const string Incoming = "wolverine_incoming_envelopes";
    public const string Outgoing = "wolverine_outgoing_envelopes";
    public const string DeadLetter = "wolverine_dead_letters";
    public const string Nodes = "wolverine_nodes";
    public const string AgentRestrictions = "wolverine_agent_restrictions";
    public const string NodeRecords = "wolverine_node_records";
}

internal sealed class WolverineIncomingEnvelope : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("status")] public string? Status { get; set; }
    [CborProperty("owner_id")] public int OwnerId { get; set; }
    [CborProperty("attempts")] public int Attempts { get; set; }
    [CborProperty("execution_time")] public DateTimeOffset ExecutionTime { get; set; }
    [CborProperty("body")] public string? Body { get; set; }
    [CborProperty("message_type")] public string? MessageType { get; set; }
    [CborProperty("destination")] public string? Destination { get; set; }
    [CborProperty("deliver_by")] public DateTimeOffset? DeliverBy { get; set; }
    [CborProperty("correlation_id")] public string? CorrelationId { get; set; }
    [CborProperty("source")] public string? Source { get; set; }
    [CborProperty("tenant_id")] public string? TenantId { get; set; }
    [CborProperty("keep_until")] public DateTimeOffset? KeepUntil { get; set; }
    [CborProperty("content_type")] public string? ContentType { get; set; }
    [CborProperty("reply_uri")] public string? ReplyUri { get; set; }
    [CborProperty("saga_id")] public string? SagaId { get; set; }
    [CborProperty("conversation_id")] public string? ConversationId { get; set; }
    [CborProperty("received_at")] public string? ReceivedAt { get; set; }
}

internal sealed class WolverineOutgoingEnvelope : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("owner_id")] public int OwnerId { get; set; }
    [CborProperty("body")] public string? Body { get; set; }
    [CborProperty("message_type")] public string? MessageType { get; set; }
    [CborProperty("destination")] public string? Destination { get; set; }
    [CborProperty("deliver_by")] public DateTimeOffset? DeliverBy { get; set; }
    [CborProperty("attempts")] public int Attempts { get; set; }
    [CborProperty("execution_time")] public DateTimeOffset ExecutionTime { get; set; }
    [CborProperty("correlation_id")] public string? CorrelationId { get; set; }
    [CborProperty("source")] public string? Source { get; set; }
    [CborProperty("tenant_id")] public string? TenantId { get; set; }
    [CborProperty("keep_until")] public DateTimeOffset? KeepUntil { get; set; }
    [CborProperty("content_type")] public string? ContentType { get; set; }
    [CborProperty("reply_uri")] public string? ReplyUri { get; set; }
    [CborProperty("saga_id")] public string? SagaId { get; set; }
    [CborProperty("conversation_id")] public string? ConversationId { get; set; }
}

internal sealed class WolverineDeadLetterEnvelope : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public string? Status { get; set; }
    public string? Body { get; set; }
    public string? MessageType { get; set; }
    public string? Source { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public string? TenantId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public string? CorrelationId { get; set; }
    public string? ContentType { get; set; }
    public string? SagaId { get; set; }
    public string? ConversationId { get; set; }
    public bool Replayable { get; set; } = true;
}

internal sealed class WolverineNode : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("node_number")]
    [JsonPropertyName("node_number")]
    public int NodeNumber { get; set; }
    [CborProperty("description")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [CborProperty("assigned_agents")]
    [JsonPropertyName("assigned_agents")]
    public List<string>? AssignedAgents { get; set; }
    [CborProperty("capabilities")]
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }
    [CborProperty("health_check_time")]
    [JsonPropertyName("health_check_time")]
    public DateTimeOffset? HealthCheckTime { get; set; }
    [CborProperty("started")]
    [JsonPropertyName("started")]
    public DateTimeOffset? Started { get; set; }
    [CborProperty("control_uri")]
    [JsonPropertyName("control_uri")]
    public string? ControlUri { get; set; }
    [CborProperty("version")]
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class WolverineNodeSequence : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("next_node_number")]
    public long NextNodeNumber { get; set; }
}

internal sealed class WolverineLeadershipLease : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("owner_node_id")]
    public string? OwnerNodeId { get; set; }

    [CborProperty("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

// RawQuery CONTENT persists native DateTimeOffset values. Keep this read model
// separate from the schema record so the authority reader remains strict about
// the coordinator's current typed representation.
internal sealed class WolverineNodeAuthorityRecord : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("node_number")]
    public int NodeNumber { get; set; }

    [CborProperty("description")]
    public string? Description { get; set; }

    [CborProperty("assigned_agents")]
    public List<string>? AssignedAgents { get; set; }

    [CborProperty("capabilities")]
    public List<string>? Capabilities { get; set; }

    [CborProperty("health_check_time")]
    public DateTimeOffset? HealthCheckTime { get; set; }

    [CborProperty("started")]
    public DateTimeOffset? Started { get; set; }

    [CborProperty("control_uri")]
    public string? ControlUri { get; set; }

    [CborProperty("version")]
    public string? Version { get; set; }
}

internal sealed class WolverineAgentRestrictions : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    [CborProperty("agent_uri")] public string? AgentUri { get; set; }
    [CborProperty("type")] public string? Type { get; set; }
    [CborProperty("node_number")] public int NodeNumber { get; set; }
}

internal sealed class WolverineNodeRecords : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public int NodeNumber { get; set; }
    public string? RecordType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Description { get; set; }
    public string? ServiceName { get; set; }
    public string? AgentUri { get; set; }
}
