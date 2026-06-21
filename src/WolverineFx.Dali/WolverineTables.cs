using SurrealDb.Net.Models;

namespace WolverineFx.Dali;

/// <summary>
/// Typed SurrealDB record for wolverine_incoming_envelopes.
/// Replaces the raw SurrealQL SchemaScript with Dali's Schema.For{T}() pipeline.
/// Extends <see cref="Record"/> for IRecord compatibility.
/// The Id property shadows Record.RecordId? Id to avoid CBOR serialization issues
/// with string-based Wolvernine envelope IDs.
/// </summary>
public sealed class WolverineIncomingEnvelopes : Record
{
    public new string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int OwnerId { get; set; }
    public DateTimeOffset ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public string Body { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
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

/// <summary>
/// Typed SurrealDB record for wolverine_outgoing_envelopes.
/// </summary>
public sealed class WolverineOutgoingEnvelopes : Record
{
    public new string Id { get; set; } = string.Empty;
    public int OwnerId { get; set; }
    public string Body { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
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

/// <summary>
/// Typed SurrealDB record for wolverine_dead_letters.
/// </summary>
public sealed class WolverineDeadLetters : Record
{
    public new string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
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

/// <summary>
/// Typed SurrealDB record for wolverine_nodes.
/// </summary>
public sealed class WolverineNodes : Record
{
    public new string Id { get; set; } = string.Empty;
    public int NodeNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public string[]? AssignedAgents { get; set; }
    public string[]? Capabilities { get; set; }
    public DateTimeOffset? HealthCheckTime { get; set; }
    public DateTimeOffset? Started { get; set; }
    public string? ControlUri { get; set; }
    public string? Version { get; set; }
}

/// <summary>
/// Typed SurrealDB record for wolverine_agent_restrictions.
/// </summary>
public sealed class WolverineAgentRestrictions : Record
{
    public new string Id { get; set; } = string.Empty;
    public string AgentUri { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int NodeNumber { get; set; }
}

/// <summary>
/// Typed SurrealDB record for wolverine_node_records.
/// </summary>
public sealed class WolverineNodeRecords : Record
{
    public new string Id { get; set; } = string.Empty;
    public int NodeNumber { get; set; }
    public string RecordType { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string? AgentUri { get; set; }
}
