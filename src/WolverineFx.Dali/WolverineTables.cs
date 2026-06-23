using System.Text.Json.Serialization;
using Dahomey.Cbor.Attributes;
using SurrealDb.Net.Json;
using SurrealDb.Net.Models;

namespace WolverineFx.Dali;

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

    public string? Status { get; set; }
    public int OwnerId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset ExecutionTime { get; set; }
    public string? Body { get; set; }
    public string? MessageType { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public string? CorrelationId { get; set; }
    public string? Source { get; set; }
    public string? TenantId { get; set; }
    public DateTimeOffset? KeepUntil { get; set; }
    public string? ContentType { get; set; }
    public string? ReplyUri { get; set; }
    public string? SagaId { get; set; }
    public string? ConversationId { get; set; }
    public string? ReceivedAt { get; set; }
}

internal sealed class WolverineOutgoingEnvelope : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public int OwnerId { get; set; }
    public string? Body { get; set; }
    public string? MessageType { get; set; }
    public string? Destination { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset ExecutionTime { get; set; }
    public string? CorrelationId { get; set; }
    public string? Source { get; set; }
    public string? TenantId { get; set; }
    public DateTimeOffset? KeepUntil { get; set; }
    public string? ContentType { get; set; }
    public string? ReplyUri { get; set; }
    public string? SagaId { get; set; }
    public string? ConversationId { get; set; }
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

    public int NodeNumber { get; set; }
    public string? Description { get; set; }
    public List<string>? AssignedAgents { get; set; }
    public List<string>? Capabilities { get; set; }
    public DateTimeOffset? HealthCheckTime { get; set; }
    public DateTimeOffset? Started { get; set; }
    public string? ControlUri { get; set; }
    public string? Version { get; set; }
}

internal sealed class WolverineAgentRestrictions : IRecord
{
    [JsonConverter(typeof(ReadOnlyRecordIdJsonConverter))]
    [CborProperty("id")]
    [CborIgnoreIfDefault]
    public RecordId? Id { get; set; }

    public string? AgentUri { get; set; }
    public string? Type { get; set; }
    public int NodeNumber { get; set; }
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
