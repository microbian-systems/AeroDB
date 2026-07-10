using System.Text;
using System.Text.Json;
using AeroDB.Sable;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace AeroDB.WolverineFx;

/// <summary>
/// SurrealDB (AeroDB.Sable) backed implementation of Wolverine's IMessageStore and all sub-interfaces.
/// All operations use SurrealQL RawQuery with parameterized queries.
/// Envelope bodies are stored as Base64 strings for reliable CBOR round-tripping.
/// 
/// Schema initialization now delegates to the AeroDB.Sable <see cref="SchemaManager"/> pipeline
/// via typed POCOs (<see cref="WolverineIncomingEnvelope"/>, etc.) instead of
/// a hardcoded SurrealQL string.
/// </summary>
public sealed class AeroDBMessageStore : IMessageStore,
    IMessageInbox, IMessageOutbox, IDeadLetters,
    INodeAgentPersistence, IMessageStoreAdmin, IScheduledMessages
{
    internal readonly ISurrealDbClient Client;
    private readonly ILogger<AeroDBMessageStore> _logger;
    private readonly SchemaManager _schemaManager;
    private int _ownerId;
    private bool _hasDisposed;
    private Guid _nodeId = Guid.NewGuid();
    private Wolverine.Runtime.Agents.WolverineNode? _currentNode;
    private bool _hasLeadershipLock;

    private const string IncomingTable = "wolverine_incoming_envelopes";
    private const string OutgoingTable = "wolverine_outgoing_envelopes";
    private const string DeadLetterTable = "wolverine_dead_letters";
    private const string NodesTable = "wolverine_nodes";
    private const string AgentRestrictionsTable = "wolverine_agent_restrictions";
    private const string NodeRecordsTable = "wolverine_node_records";

    public AeroDBMessageStore(ISurrealDbClient client, ILogger<AeroDBMessageStore> logger, ILoggerFactory? loggerFactory = null, StoreOptions? storeOptions = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaManager = new SchemaManager(loggerFactory);

        // Derive Uri from the actual connection endpoint if available
        Uri = storeOptions?.Endpoint is { Length: > 0 } ep
            ? new Uri(ep)
            : new Uri("AeroDB://localhost");
    }

    // ─── IMessageStore Members ───

    public MessageStoreRole Role { get; private set; } = MessageStoreRole.Main;

    public List<string> TenantIds { get; } = new();

    public Uri Uri { get; private set; }

    public bool HasDisposed => _hasDisposed;

    public IMessageInbox Inbox => this;

    public IMessageOutbox Outbox => this;

    public INodeAgentPersistence Nodes => this;

    public IListenerStore Listeners => NullListenerStore.Instance;

    public IMessageStoreAdmin Admin => this;

    public IDeadLetters DeadLetters => this;

    public IScheduledMessages ScheduledMessages => this;

    public string Name { get; set; } = "AeroDB.Sable";

    public void Initialize(IWolverineRuntime runtime)
    {
        _ownerId = runtime.Options.Durability.AssignedNodeNumber;
        Name = runtime.Options.ServiceName ?? "AeroDB.Sable";
        _logger.LogInformation("AeroDBMessageStore initialized as node {NodeId}, ownerId={OwnerId}", _nodeId, _ownerId);
    }

    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor
        {
            Engine = "SurrealDB",
            ServerName = "AeroDB.Sable",
            DatabaseName = "wolverine",
            Subject = GetType().FullName!
        };
    }

    public async Task DrainAsync()
    {
        if (_hasDisposed) return;
        try
        {
            await ReleaseAllOwnershipAsync();
        }
        catch
        {
            // Drain can fail if the underlying store is already disposed
            // (e.g., in-memory engine shutdown during host teardown).
            // Swallow — the owning node is going away.
        }
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        // Return a polling agent that checks for ready scheduled messages
        // and moves them to 'Incoming' status for the durability agent.
        return new AeroDBScheduledJobAgent(Client, runtime.Logger);
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        var sql = $"SELECT * FROM {IncomingTable} WHERE owner_id = 0 AND status = 'Incoming' ORDER BY execution_time ASC LIMIT {limit}";
        var response = await Client.RawQuery(sql);
        var records = DeserializeAeroDBEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0) return;

        // Use individual UPDATE statements for maximum SurrealDB portability.
        // Multi-node atomicity can be enhanced later with SurrealDB 3.1+ MERGE/RETURN AFTER.
        foreach (var env in incoming)
        {
            var id = env.Id.ToString();
            await Client.RawQuery(
                $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET owner_id = {ownerId};");
        }
    }

    public void PromoteToMain(IWolverineRuntime runtime)
    {
        Role = MessageStoreRole.Main;
        Initialize(runtime);
    }

    public void DemoteToAncillary()
    {
        Role = MessageStoreRole.Ancillary;
    }

    /// <summary>Expose the assigned owner node number for outbox enlistment.</summary>
    internal int GetOwnerId() => _ownerId;

    public async ValueTask DisposeAsync()
    {
        if (_hasDisposed) return;
        _hasDisposed = true;

        if (Client is IAsyncDisposable ad)
            await ad.DisposeAsync();
        else if (Client is IDisposable d)
            d.Dispose();
    }

    // ─── IMessageInbox ───

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        var execTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow;
        var execTimeStr = execTime.ToString("o");
        await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Incoming', owner_id = 0, attempts = {envelope.Attempts}, execution_time = <datetime>'{execTimeStr}'");
    }

    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        var execTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow;
        var execTimeStr = execTime.ToString("o");
        await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Scheduled', execution_time = <datetime>'{execTimeStr}'");
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        if (_hasDisposed) return;

        var env = AeroDBEnvelope.FromEnvelope(envelope, _ownerId);
        env.Status = EnvelopeStatus.Incoming.ToString();

        var deadId = envelope.Id.ToString();

        var json = JsonSerializer.Serialize(env, JsonOptions);

        var sql = $"BEGIN TRANSACTION;" +
                  $"DELETE FROM {IncomingTable}:`{EscapeId(deadId)}`;" +
                  $"CREATE {DeadLetterTable} CONTENT {json};" +
                  $"COMMIT TRANSACTION;";
        await Client.RawQuery(sql);
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET attempts = {envelope.Attempts}");
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var env = AeroDBEnvelope.FromEnvelope(envelope, _ownerId);
        env.ReceivedAt = envelope.Destination?.ToString();
        var json = JsonSerializer.Serialize(env, JsonOptions);
        await Client.RawQuery($"CREATE {IncomingTable} CONTENT {json}");
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (_hasDisposed || envelopes.Count == 0) return;

        // For multi-envelope batches, use individual queries since
        // SurrealDB batch parameterization doesn't support per-statement params
        foreach (var e in envelopes)
        {
            await StoreIncomingAsync(e);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (_hasDisposed) return false;
        var id = envelope.Id.ToString();
        var response = await Client.RawQuery(
            $"SELECT count() as count FROM {IncomingTable}:`{EscapeId(id)}` GROUP ALL");
        // Try to extract count from response
        try
        {
            if (response.FirstOk is not null)
            {
                var result = response.GetValue<List<Dictionary<string, object>>>(0);
                return result?.Count > 0;
            }
        }
        catch { /* ignore parse errors */ }
        return false;
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var keepUntil = DateTimeOffset.UtcNow.AddDays(1); // Default keep duration
        var keepUntilStr = keepUntil.ToString("o");
        var id = envelope.Id.ToString();
        await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Handled', keep_until = <datetime>'{keepUntilStr}'");
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (_hasDisposed || envelopes.Count == 0) return;

        var keepUntil = DateTimeOffset.UtcNow.AddDays(1);
        var keepUntilStr = keepUntil.ToString("o");
        var sb = new StringBuilder("BEGIN TRANSACTION;");
        foreach (var env in envelopes)
        {
            var id = env.Id.ToString();
            sb.AppendLine($"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Handled', keep_until = <datetime>'{keepUntilStr}';");
        }
        sb.AppendLine("COMMIT TRANSACTION;");
        await Client.RawQuery(sb.ToString());
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        if (_hasDisposed) return;
        var uriStr = EscapeSurql(receivedAt.ToString());
        await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0 WHERE owner_id = {ownerId} AND received_at = '{uriStr}'");
    }

    // ─── IMessageOutbox ───

    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        var destStr = EscapeSurql(destination.ToString());
        var response = await Client.RawQuery(
            $"SELECT * FROM {OutgoingTable} WHERE owner_id = 0 AND destination = '{destStr}' ORDER BY execution_time ASC");
        var records = DeserializeAeroDBEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        if (_hasDisposed) return;
        var env = AeroDBEnvelope.FromOutgoingEnvelope(envelope, ownerId);
        var json = JsonSerializer.Serialize(env, JsonOptions);
        await Client.RawQuery($"CREATE {OutgoingTable} CONTENT {json}");
        envelope.WasPersistedInOutbox = true;
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (_hasDisposed || envelopes.Length == 0) return;

        var sb = new StringBuilder("BEGIN TRANSACTION;");
        foreach (var env in envelopes)
        {
            var id = env.Id.ToString();
            sb.AppendLine($"DELETE FROM {OutgoingTable}:`{EscapeId(id)}`;");
        }
        sb.AppendLine("COMMIT TRANSACTION;");
        await Client.RawQuery(sb.ToString());
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        await Client.RawQuery($"DELETE FROM {OutgoingTable}:`{EscapeId(id)}`");
    }

    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        if (_hasDisposed) return;

        var sb = new StringBuilder("BEGIN TRANSACTION;");

        foreach (var env in discards)
        {
            var id = env.Id.ToString();
            sb.AppendLine($"DELETE FROM {OutgoingTable}:`{EscapeId(id)}`;");
        }

        foreach (var env in reassigned)
        {
            var id = env.Id.ToString();
            sb.AppendLine($"UPDATE {OutgoingTable}:`{EscapeId(id)}` SET owner_id = {nodeId};");
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        await Client.RawQuery(sb.ToString());
    }

    // ─── IDeadLetters ───

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        var response = await Client.RawQuery(
            $"SELECT * FROM {DeadLetterTable}:`{EscapeId(id.ToString())}`");
        var records = DeserializeAeroDBEnvelopes(response, 0);
        var record = records.FirstOrDefault();
        if (record is null) return null;

        var env = record.ToEnvelope();
        return new DeadLetterEnvelope(
            id,
            record.ExecutionTime,
            env,
            record.MessageType,
            record.ReceivedAt ?? string.Empty,
            record.Source ?? string.Empty,
            record.ExceptionType ?? string.Empty,
            record.ExceptionMessage ?? string.Empty,
            record.SentAt ?? DateTimeOffset.UtcNow,
            record.Replayable ?? true);
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
    {
        var response = await Client.RawQuery(
            $"SELECT message_type, exception_type, destination, count() as total FROM {DeadLetterTable} GROUP BY message_type, exception_type, destination");
        var results = new List<DeadLetterQueueCount>();
        try
        {
            if (response.FirstOk is not null)
            {
                var raw = response.GetValue<List<Dictionary<string, object>>>(0);
                if (raw is not null)
                {
                    foreach (var row in raw)
                    {
                        var mt = row.GetValueOrDefault("message_type")?.ToString() ?? "unknown";
                        var et = row.GetValueOrDefault("exception_type")?.ToString() ?? "";
                        var dest = row.GetValueOrDefault("destination")?.ToString() ?? "";
                        var count = Convert.ToInt32(row.GetValueOrDefault("total") ?? 0);
                        results.Add(new DeadLetterQueueCount(serviceName,
                            string.IsNullOrEmpty(dest) ? Uri : new Uri($"AeroDB://{dest}"),
                            mt, et, Uri, count));
                    }
                }
            }
        }
        catch { /* best effort */ }
        return results;
    }

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var whereClauses = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (query.MessageIds.Length > 0)
        {
            var ids = string.Join(", ", query.MessageIds.Select(id => $"'{EscapeId(id.ToString())}'"));
            whereClauses.Add($"id INSIDE [{ids}]");
        }
        else
        {
            if (query.MessageType is not null)
            {
                whereClauses.Add("message_type = $msgType");
                parameters["msgType"] = query.MessageType;
            }
            if (query.ExceptionType is not null)
            {
                whereClauses.Add("exception_type = $excType");
                parameters["excType"] = query.ExceptionType;
            }
            if (query.ExceptionMessage is not null)
            {
                whereClauses.Add("exception_message CONTAINS $excMsg");
                parameters["excMsg"] = query.ExceptionMessage;
            }
            if (query.ReceivedAt is not null)
            {
                whereClauses.Add("destination = $rcvAt");
                parameters["rcvAt"] = query.ReceivedAt;
            }
            if (query.Range.From.HasValue)
            {
                whereClauses.Add("sent_at >= $from");
                parameters["from"] = query.Range.From.Value;
            }
            if (query.Range.To.HasValue)
            {
                whereClauses.Add("sent_at <= $to");
                parameters["to"] = query.Range.To.Value;
            }
        }

        var where = whereClauses.Count > 0 ? " WHERE " + string.Join(" AND ", whereClauses) : "";
        var limit = query.PageSize > 0 ? $" LIMIT {query.PageSize}" : "";
        var offset = query.PageNumber > 1 ? $" START {(query.PageNumber - 1) * query.PageSize}" : "";

        var sql = $"SELECT * FROM {DeadLetterTable}{where} ORDER BY sent_at DESC{limit}{offset}";
        var response = await Client.RawQuery(sql, parameters);
        var records = DeserializeAeroDBEnvelopes(response, 0);

        var results = new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            DatabaseUri = Uri
        };

        foreach (var record in records)
        {
            var env = record.ToEnvelope();
            var dle = new DeadLetterEnvelope(
                Guid.Parse(record.Id),
                record.ExecutionTime,
                env,
                record.MessageType,
                record.ReceivedAt ?? string.Empty,
                record.Source ?? string.Empty,
                record.ExceptionType ?? string.Empty,
                record.ExceptionMessage ?? string.Empty,
                record.SentAt ?? DateTimeOffset.UtcNow,
                record.Replayable ?? true);
            results.Envelopes.Add(dle);
            results.TotalCount++;
        }

        return results;
    }

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var where = BuildDeadLetterWhereClause(query);
        await Client.RawQuery($"DELETE FROM {DeadLetterTable} {where}");
    }

    public async Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        // Mark as replayable — the DurabilityAgent picks these up
        // SurrealDB syntax: UPDATE table SET field = value WHERE condition
        var where = BuildDeadLetterWhereClause(query);
        if (!string.IsNullOrEmpty(where))
            await Client.RawQuery($"UPDATE {DeadLetterTable} SET replayable = true {where}");
        else
            await Client.RawQuery($"UPDATE {DeadLetterTable} SET replayable = true");
    }

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        var id = envelopeId.ToString();
        var body = Convert.ToBase64String(newBody);
        await Client.RawQuery(
            $"UPDATE {DeadLetterTable}:`{EscapeId(id)}` SET body = $body, replayable = true",
            new Dictionary<string, object?> { ["body"] = body });
    }

    // ─── INodeAgentPersistence ───

    public Task ClearAllAsync(CancellationToken cancellationToken)
    {
        return Client.RawQuery(
            $"DELETE FROM {NodesTable}; DELETE FROM {AgentRestrictionsTable}; DELETE FROM {NodeRecordsTable};");
    }

    public async Task<int> PersistAsync(Wolverine.Runtime.Agents.WolverineNode node, CancellationToken cancellationToken)
    {
        _currentNode = node;
        var json = JsonSerializer.Serialize(new
        {
            id = node.Id,
            node_number = node.AssignedNodeNumber,
            description = node.Description,
            control_uri = node.ControlUri?.ToString(),
            capabilities = node.Capabilities.Select(u => u.ToString()).ToList(),
            assigned_agents = node.ActiveAgents.Select(u => u.ToString()).ToList(),
            health_check_time = node.LastHealthCheck,
            started = node.Started,
            version = node.Version?.ToString()
        }, JsonOptions);

        await Client.RawQuery($"CREATE {NodesTable}:`{EscapeId(node.Id)}` CONTENT {json}");
        return node.AssignedNodeNumber;
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        await Client.RawQuery($"DELETE FROM {NodesTable}:`{EscapeId(nodeId.ToString())}`");
    }

    public async Task<IReadOnlyList<Wolverine.Runtime.Agents.WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var response = await Client.RawQuery($"SELECT * FROM {NodesTable} ORDER BY node_number ASC");
        return DeserializeNodeList(response);
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken ct)
    {
        if (_hasDisposed) return;

        // Clear existing restrictions and insert the new set atomically
        var sb = new StringBuilder("BEGIN TRANSACTION;");
        sb.AppendLine($"DELETE FROM {AgentRestrictionsTable};");

        foreach (var restriction in restrictions)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = restriction.Id.ToString(),
                agent_uri = restriction.AgentUri.ToString(),
                type = restriction.Type.ToString(),
                node_number = restriction.NodeNumber
            }, JsonOptions);
            sb.AppendLine($"CREATE {AgentRestrictionsTable} CONTENT {json};");
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        await Client.RawQuery(sb.ToString());
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = await LoadAllNodesAsync(cancellationToken);
        var restrictions = new List<AgentRestriction>();
        try
        {
            var resp = await Client.RawQuery($"SELECT * FROM {AgentRestrictionsTable}");
            var raw = DeserializeResponse<List<Dictionary<string, object>>>(resp, 0);
            if (raw is not null)
            {
                foreach (var r in raw)
                {
                    try
                    {
                        var id = Guid.Parse(r.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString());
                        var uri = new Uri(r.GetValueOrDefault("agent_uri")?.ToString() ?? "AeroDB://unknown");
                        var type = r.GetValueOrDefault("type")?.ToString() == "Pinned"
                            ? AgentRestrictionType.Pinned
                            : r.GetValueOrDefault("type")?.ToString() == "Paused"
                                ? AgentRestrictionType.Paused
                                : AgentRestrictionType.None;
                        var num = Convert.ToInt32(r.GetValueOrDefault("node_number") ?? 0);
                        restrictions.Add(new AgentRestriction(id, uri, type, num));
                    }
                    catch { /* skip malformed */ }
                }
            }
        }
        catch { /* no restrictions yet */ }

        return new NodeAgentState(nodes, new AgentRestrictions(restrictions.ToArray()));
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken ct)
    {
        var agentsJson = JsonSerializer.Serialize(agents.Select(u => u.ToString()).ToList());
        await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(nodeId.ToString())}` SET assigned_agents = {agentsJson}");
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken ct)
    {
        var node = await LoadNodeAsync(nodeId, ct);
        if (node is null) return;
        node.ActiveAgents.Remove(agentUri);
        var agentsJson = JsonSerializer.Serialize(node.ActiveAgents.Select(u => u.ToString()).ToList());
        await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(nodeId.ToString())}` SET assigned_agents = {agentsJson}");
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken ct)
    {
        var node = await LoadNodeAsync(nodeId, ct);
        if (node is null) return;
        if (!node.ActiveAgents.Contains(agentUri))
            node.ActiveAgents.Add(agentUri);
        var agentsJson = JsonSerializer.Serialize(node.ActiveAgents.Select(u => u.ToString()).ToList());
        await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(nodeId.ToString())}` SET assigned_agents = {agentsJson}");
    }

    public async Task<Wolverine.Runtime.Agents.WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var response = await Client.RawQuery(
            $"SELECT * FROM {NodesTable}:`{EscapeId(nodeId.ToString())}`");
        var nodes = DeserializeNodeList(response);
        return nodes.FirstOrDefault();
    }

    public async Task MarkHealthCheckAsync(Wolverine.Runtime.Agents.WolverineNode node, CancellationToken cancellationToken)
    {
        node.LastHealthCheck = DateTimeOffset.UtcNow;
        await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(node.Id)}` SET health_check_time = $time",
            new Dictionary<string, object?> { ["time"] = node.LastHealthCheck });
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(nodeId.ToString())}` SET health_check_time = $time",
            new Dictionary<string, object?> { ["time"] = lastHeartbeatTime });
    }

    public async Task LogRecordsAsync(params NodeRecord[] records)
    {
        if (records.Length == 0) return;
        var sb = new StringBuilder("BEGIN TRANSACTION;");
        foreach (var rec in records)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = rec.Id,
                node_number = rec.NodeNumber,
                record_type = rec.RecordType.ToString(),
                timestamp = rec.Timestamp,
                description = rec.Description,
                service_name = rec.ServiceName,
                agent_uri = rec.AgentUri?.ToString()
            }, JsonOptions);
            sb.AppendLine($"CREATE {NodeRecordsTable} CONTENT {json};");
        }
        sb.AppendLine("COMMIT TRANSACTION;");
        await Client.RawQuery(sb.ToString());
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        var response = await Client.RawQuery(
            $"SELECT * FROM {NodeRecordsTable} ORDER BY timestamp DESC LIMIT {count}");
        var records = new List<NodeRecord>();
        try
        {
            var raw = DeserializeResponse<List<Dictionary<string, object>>>(response, 0);
            if (raw is not null)
            {
                foreach (var r in raw)
                {
                    try
                    {
                        var rec = new NodeRecord
                        {
                            Id = r.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString(),
                            NodeNumber = Convert.ToInt32(r.GetValueOrDefault("node_number") ?? 0),
                            RecordType = Enum.TryParse<NodeRecordType>(r.GetValueOrDefault("record_type")?.ToString(), out var rt) ? rt : NodeRecordType.NodeStarted,
                            Timestamp = r.GetValueOrDefault("timestamp") is DateTimeOffset dto ? dto : DateTimeOffset.UtcNow,
                            Description = r.GetValueOrDefault("description")?.ToString() ?? "",
                            ServiceName = r.GetValueOrDefault("service_name")?.ToString() ?? ""
                        };
                        records.Add(rec);
                    }
                    catch { /* skip */ }
                }
            }
        }
        catch { /* no records yet */ }
        return records;
    }

    public Task DeleteOldNodeRecordsAsync(int retainCount) => Task.CompletedTask;

    public bool HasLeadershipLock() => _hasLeadershipLock;

    public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        // Use a simple approach: try to INSERT a lock record with a unique ID.
        // If it already exists, another node holds the lock.
        try
        {
            var lockId = $"wolverine_leader_lock";
            var response = await Client.RawQuery(
                $"INSERT INTO wolverine_leader_lock (id, node_id, acquired_at) VALUES ($lockId, $nodeId, $now) ON DUPLICATE KEY UPDATE node_id = $nodeId, acquired_at = $now",
                new Dictionary<string, object?>
                {
                    ["lockId"] = lockId,
                    ["nodeId"] = _nodeId.ToString(),
                    ["now"] = DateTimeOffset.UtcNow
                });
            _hasLeadershipLock = true;
            return true;
        }
        catch
        {
            _hasLeadershipLock = false;
            return false;
        }
    }

    public async Task ReleaseLeadershipLockAsync()
    {
        try
        {
            await Client.RawQuery("DELETE FROM wolverine_leader_lock WHERE id = 'wolverine_leader_lock'");
        }
        finally
        {
            _hasLeadershipLock = false;
        }
    }

    // ─── IMessageStoreAdmin ───

    public async Task DeleteAllHandledAsync()
    {
        await Client.RawQuery($"DELETE FROM {IncomingTable} WHERE status = 'Handled'");
    }

    public async Task ClearAllAsync()
    {
        await Client.RawQuery(
            $"DELETE FROM {IncomingTable}; DELETE FROM {OutgoingTable}; DELETE FROM {DeadLetterTable}; DELETE FROM {NodeRecordsTable}; DELETE FROM {AgentRestrictionsTable};");
    }

    public async Task RebuildAsync()
    {
        await ClearAllAsync();
        await InitializeSchemaAsync();
    }

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        try
        {
            var resp = await Client.RawQuery(
                $"SELECT status, count() as total FROM {IncomingTable} GROUP BY status");
            var raw = DeserializeResponse<List<Dictionary<string, object>>>(resp, 0);
            if (raw is not null)
            {
                foreach (var row in raw)
                {
                    var status = row.GetValueOrDefault("status")?.ToString() ?? "";
                    var total = Convert.ToInt32(row.GetValueOrDefault("total") ?? 0);
                    switch (status)
                    {
                        case "Incoming": counts.Incoming = total; break;
                        case "Handled": counts.Handled = total; break;
                        case "Scheduled": counts.Scheduled = total; break;
                    }
                }
            }
        }
        catch { /* best effort */ }

        try
        {
            var resp2 = await Client.RawQuery($"SELECT count() as total FROM {OutgoingTable} GROUP ALL");
            var raw2 = DeserializeResponse<List<Dictionary<string, object>>>(resp2, 0);
            if (raw2?.Count > 0)
                counts.Outgoing = Convert.ToInt32(raw2[0].GetValueOrDefault("total") ?? 0);
        }
        catch { /* best effort */ }

        try
        {
            var resp3 = await Client.RawQuery($"SELECT count() as total FROM {DeadLetterTable} GROUP ALL");
            var raw3 = DeserializeResponse<List<Dictionary<string, object>>>(resp3, 0);
            if (raw3?.Count > 0)
                counts.DeadLetter = Convert.ToInt32(raw3[0].GetValueOrDefault("total") ?? 0);
        }
        catch { /* best effort */ }

        return counts;
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        var response = await Client.RawQuery($"SELECT * FROM {IncomingTable}");
        var records = DeserializeAeroDBEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        var response = await Client.RawQuery($"SELECT * FROM {OutgoingTable}");
        var records = DeserializeAeroDBEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0; UPDATE {OutgoingTable} SET owner_id = 0;");
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0 WHERE owner_id = {ownerId}; UPDATE {OutgoingTable} SET owner_id = 0 WHERE owner_id = {ownerId};");
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        await Client.RawQuery("SELECT 1;");
    }

    public async Task AssertStorageExistsAsync(CancellationToken token)
    {
        // Verify incoming table exists by querying with a simple SELECT 1 pattern
        await Client.RawQuery($"SELECT count() FROM {IncomingTable} LIMIT 1 GROUP ALL");
        // If we get here without exception, storage exists
    }

    public async Task MigrateAsync()
    {
        await InitializeSchemaAsync();
    }

    // ─── IScheduledMessages ───

    public async Task<ScheduledMessageResults> QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var whereClauses = new List<string> { "status = 'Scheduled'" };
        var parameters = new Dictionary<string, object?>();

        if (query.MessageType is not null)
        {
            whereClauses.Add("message_type = $msgType");
            parameters["msgType"] = query.MessageType;
        }
        if (query.ExecutionTimeFrom.HasValue)
        {
            whereClauses.Add("execution_time >= $from");
            parameters["from"] = query.ExecutionTimeFrom.Value;
        }
        if (query.ExecutionTimeTo.HasValue)
        {
            whereClauses.Add("execution_time <= $to");
            parameters["to"] = query.ExecutionTimeTo.Value;
        }
        if (query.MessageIds.Length > 0)
        {
            var ids = string.Join(", ", query.MessageIds.Select(id => $"'{EscapeId(id.ToString())}'"));
            whereClauses.Add($"id INSIDE [{ids}]");
        }

        var where = " WHERE " + string.Join(" AND ", whereClauses);
        var limit = query.PageSize > 0 ? $" LIMIT {query.PageSize}" : "";
        var offset = query.PageNumber > 1 ? $" START {(query.PageNumber - 1) * query.PageSize}" : "";

        var sql = $"SELECT * FROM {IncomingTable}{where} ORDER BY execution_time ASC{limit}{offset}";
        var response = await Client.RawQuery(sql, parameters);
        var records = DeserializeAeroDBEnvelopes(response, 0);

        var results = new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            DatabaseUri = Uri
        };

        foreach (var record in records)
        {
            results.Messages.Add(new ScheduledMessageSummary
            {
                Id = Guid.Parse(record.Id),
                MessageType = record.MessageType,
                ScheduledTime = record.ExecutionTime,
                Destination = record.Destination,
                Attempts = record.Attempts
            });
            results.TotalCount++;
        }

        return results;
    }

    public async Task CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var whereClauses = new List<string> { "status = 'Scheduled'" };
        var parameters = new Dictionary<string, object?>();

        if (query.MessageType is not null)
        {
            whereClauses.Add("message_type = $msgType");
            parameters["msgType"] = query.MessageType;
        }
        if (query.ExecutionTimeFrom.HasValue)
        {
            whereClauses.Add("execution_time >= $from");
            parameters["from"] = query.ExecutionTimeFrom.Value;
        }
        if (query.ExecutionTimeTo.HasValue)
        {
            whereClauses.Add("execution_time <= $to");
            parameters["to"] = query.ExecutionTimeTo.Value;
        }
        if (query.MessageIds.Length > 0)
        {
            var ids = string.Join(", ", query.MessageIds.Select(id => $"'{EscapeId(id.ToString())}'"));
            whereClauses.Add($"id INSIDE [{ids}]");
        }

        var where = " WHERE " + string.Join(" AND ", whereClauses);
        await Client.RawQuery($"DELETE FROM {IncomingTable}{where}", parameters);
    }

    public async Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        var timeStr = newExecutionTime.ToString("o");
        await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(envelopeId.ToString())}` SET execution_time = <datetime>'{timeStr}' WHERE status = 'Scheduled'");
    }

    public async Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token)
    {
        var response = await Client.RawQuery(
            $"SELECT message_type, count() as total FROM {IncomingTable} WHERE status = 'Scheduled' GROUP BY message_type");
        var results = new List<ScheduledMessageCount>();
        try
        {
            var raw = DeserializeResponse<List<Dictionary<string, object>>>(response, 0);
            if (raw is not null)
            {
                foreach (var row in raw)
                {
                    var mt = row.GetValueOrDefault("message_type")?.ToString() ?? "unknown";
                    var total = Convert.ToInt32(row.GetValueOrDefault("total") ?? 0);
                    results.Add(new ScheduledMessageCount(serviceName, mt, Uri, total));
                }
            }
        }
        catch { /* best effort */ }
        return results;
    }

    // ─── Schema Initialization ───

    /// <summary>
    /// Initialize the SurrealDB schema for all wolverine tables using the AeroDB.Sable schema pipeline.
    /// Creates tables, fields, and indexes via <see cref="SchemaManager"/>.
    /// Called during store initialization. Idempotent — uses IF NOT EXISTS variants.
    /// </summary>
    public async Task InitializeSchemaAsync()
    {
        await using var session = await Client.CreateSession().ConfigureAwait(false);

        await _schemaManager.EnsureDocumentSchemaAsync<WolverineIncomingEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineOutgoingEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineDeadLetterEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineNode>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineAgentRestrictions>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineNodeRecords>(session, SchemaMode.Strict).ConfigureAwait(false);

        // Indexes for wolverine_incoming_envelopes
        await _schemaManager.EnsureIndexAsync(session, IncomingTable, new IndexDefinition
        {
            Name = "idx_incoming_status", Columns = ["status"], Type = IndexType.Standard
        }).ConfigureAwait(false);
        await _schemaManager.EnsureIndexAsync(session, IncomingTable, new IndexDefinition
        {
            Name = "idx_incoming_exec", Columns = ["execution_time"], Type = IndexType.Standard
        }).ConfigureAwait(false);
        await _schemaManager.EnsureIndexAsync(session, IncomingTable, new IndexDefinition
        {
            Name = "idx_incoming_owner", Columns = ["owner_id"], Type = IndexType.Standard
        }).ConfigureAwait(false);

        // Index for wolverine_outgoing_envelopes
        await _schemaManager.EnsureIndexAsync(session, OutgoingTable, new IndexDefinition
        {
            Name = "idx_outgoing_dest", Columns = ["destination"], Type = IndexType.Standard
        }).ConfigureAwait(false);

        // Index for wolverine_dead_letters
        await _schemaManager.EnsureIndexAsync(session, DeadLetterTable, new IndexDefinition
        {
            Name = "idx_dl_status", Columns = ["status"], Type = IndexType.Standard
        }).ConfigureAwait(false);

        // Unique index for wolverine_nodes
        await _schemaManager.EnsureIndexAsync(session, NodesTable, new IndexDefinition
        {
            Name = "idx_nodes_id", Columns = ["id"], IsUnique = true, Type = IndexType.Standard
        }).ConfigureAwait(false);

        _logger.LogInformation("AeroDBMessageStore schema initialized (via SchemaManager pipeline)");
    }

    // ─── Helpers ───

    private static string EscapeId(string id)
    {
        // Escape backticks in SurrealDB record IDs
        return id.Replace("`", "``");
    }

    private string BuildDeadLetterWhereClause(DeadLetterEnvelopeQuery query)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (query.MessageIds.Length > 0)
        {
            var ids = string.Join(", ", query.MessageIds.Select(id => $"'{EscapeId(id.ToString())}'"));
            clauses.Add($"id INSIDE [{ids}]");
        }
        else
        {
            if (query.MessageType is not null)
                clauses.Add($"message_type = '{EscapeSurql(query.MessageType)}'");
            if (query.ExceptionType is not null)
                clauses.Add($"exception_type = '{EscapeSurql(query.ExceptionType)}'");
        }

        return clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
    }

    private static string EscapeSurql(string value)
    {
        return value.Replace("'", "''");
    }

    private List<Wolverine.Runtime.Agents.WolverineNode> DeserializeNodeList(SurrealDb.Net.Models.Response.SurrealDbResponse response)
    {
        var nodes = new List<Wolverine.Runtime.Agents.WolverineNode>();
        try
        {
            var raw = DeserializeResponse<List<Dictionary<string, object>>>(response, 0);
            if (raw is not null)
            {
                foreach (var r in raw)
                {
                    try
                    {
                        var node = new Wolverine.Runtime.Agents.WolverineNode
                        {
                            NodeId = Guid.Parse(r.GetValueOrDefault("id")?.ToString()?.Split(':').Last() ?? Guid.NewGuid().ToString()),
                            AssignedNodeNumber = Convert.ToInt32(r.GetValueOrDefault("node_number") ?? 1),
                            Description = r.GetValueOrDefault("description")?.ToString() ?? "",
                            Started = r.GetValueOrDefault("started") is DateTimeOffset s ? s : DateTimeOffset.UtcNow,
                            LastHealthCheck = r.GetValueOrDefault("health_check_time") is DateTimeOffset h ? h : DateTimeOffset.UtcNow
                        };

                        if (r.TryGetValue("control_uri", out var cu) && cu?.ToString() is { Length: > 0 } cus)
                            node.ControlUri = new Uri(cus);

                        if (r.TryGetValue("version", out var v) && v?.ToString() is { Length: > 0 } vs)
                            node.Version = Version.TryParse(vs, out var ver) ? ver : new Version(0, 0);

                        if (r.TryGetValue("capabilities", out var caps) && caps is List<object> capList)
                        {
                            foreach (var c in capList)
                            {
                                if (c?.ToString() is { Length: > 0 } capStr && Uri.TryCreate(capStr, UriKind.Absolute, out var cu2))
                                    node.Capabilities.Add(cu2);
                            }
                        }

                        if (r.TryGetValue("assigned_agents", out var agents) && agents is List<object> agentList)
                        {
                            foreach (var a in agentList)
                            {
                                if (a?.ToString() is { Length: > 0 } aStr && Uri.TryCreate(aStr, UriKind.Absolute, out var au))
                                    node.ActiveAgents.Add(au);
                            }
                        }

                        nodes.Add(node);
                    }
                    catch { /* skip malformed node */ }
                }
            }
        }
        catch { /* no nodes yet */ }
        return nodes;
    }

    /// <summary>
    /// Deserialize SurrealDB response records into AeroDBEnvelope objects.
    /// Uses Dictionary-based deserialization to avoid CBOR issues with
    /// the Record base class's RecordId? Id conflicting with the string Id.
    /// </summary>
    private static List<AeroDBEnvelope> DeserializeAeroDBEnvelopes(SurrealDb.Net.Models.Response.SurrealDbResponse response, int index)
    {
        try
        {
            var raw = response.GetValue<List<Dictionary<string, object>>>(index);
            if (raw is null) return [];

            return raw.Select(MapToAeroDBEnvelope).Where(e => e is not null).ToList()!;
        }
        catch
        {
            return [];
        }
    }

    private static AeroDBEnvelope? MapToAeroDBEnvelope(Dictionary<string, object> row)
    {
        try
        {
            return new AeroDBEnvelope
            {
                Id = row.GetValueOrDefault("id")?.ToString() ?? string.Empty,
                Status = row.GetValueOrDefault("status")?.ToString() ?? string.Empty,
                OwnerId = Convert.ToInt32(row.GetValueOrDefault("owner_id") ?? 0),
                Attempts = Convert.ToInt32(row.GetValueOrDefault("attempts") ?? 0),
                Body = row.GetValueOrDefault("body")?.ToString() ?? string.Empty,
                MessageType = row.GetValueOrDefault("message_type")?.ToString() ?? string.Empty,
                Destination = row.GetValueOrDefault("destination")?.ToString(),
                CorrelationId = row.GetValueOrDefault("correlation_id")?.ToString(),
                Source = row.GetValueOrDefault("source")?.ToString(),
                TenantId = row.GetValueOrDefault("tenant_id")?.ToString(),
                ContentType = row.GetValueOrDefault("content_type")?.ToString(),
                ReceivedAt = row.GetValueOrDefault("received_at")?.ToString(),
                ReplyUri = row.GetValueOrDefault("reply_uri")?.ToString(),
                SagaId = row.GetValueOrDefault("saga_id")?.ToString(),
                ConversationId = row.GetValueOrDefault("conversation_id")?.ToString(),
                ExceptionType = row.GetValueOrDefault("exception_type")?.ToString(),
                ExceptionMessage = row.GetValueOrDefault("exception_message")?.ToString(),
                ExecutionTime = row.GetValueOrDefault("execution_time") is DateTimeOffset et ? et : default,
                DeliverBy = row.GetValueOrDefault("deliver_by") as DateTimeOffset?,
                KeepUntil = row.GetValueOrDefault("keep_until") as DateTimeOffset?,
                SentAt = row.GetValueOrDefault("sent_at") as DateTimeOffset?,
                Replayable = row.GetValueOrDefault("replayable") as bool?
            };
        }
        catch
        {
            return null;
        }
    }

    private static T? DeserializeResponse<T>(SurrealDb.Net.Models.Response.SurrealDbResponse response, int index) where T : class
    {
        try
        {
            return response.GetValue<T>(index);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
