using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using AeroDB.Sable;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Errors;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;
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
    private readonly StoreOptions? _storeOptions;
    private readonly bool _ownsClient;
    private readonly object _disposeGate = new();
    private readonly string _namespace;
    private readonly string _database;
    private bool _hasDisposed;
    private Task? _disposeTask;
    private readonly AeroDBRuntimeNodeIdentity _runtimeIdentity;
    private readonly SemaphoreSlim _leadershipGate = new(1, 1);
    private readonly object _leadershipStateGate = new();
    private long? _leadershipDeadline;
    private bool _hasLeadershipLock;

    private const string IncomingTable = "wolverine_incoming_envelopes";
    private const string OutgoingTable = "wolverine_outgoing_envelopes";
    private const string DeadLetterTable = "wolverine_dead_letters";
    private const string NodesTable = "wolverine_nodes";
    private const string AgentRestrictionsTable = "wolverine_agent_restrictions";
    private const string NodeRecordsTable = "wolverine_node_records";
    private const string NodeSequenceTable = "wolverine_node_sequence";
    private const string LeadershipLeaseTable = "wolverine_leadership_lease";
    private const string OutgoingSelectSql = "SELECT * FROM wolverine_outgoing_envelopes";

    public AeroDBMessageStore(ISurrealDbClient client, ILogger<AeroDBMessageStore> logger, ILoggerFactory? loggerFactory = null, StoreOptions? storeOptions = null)
        : this(client, logger, loggerFactory, storeOptions, ownsClient: true)
    {
    }

    internal AeroDBMessageStore(
        ISurrealDbClient client,
        ILogger<AeroDBMessageStore> logger,
        ILoggerFactory? loggerFactory,
        StoreOptions? storeOptions,
        bool ownsClient,
        Uri? storeIdentity = null,
        AeroDBRuntimeNodeIdentity? runtimeIdentity = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaManager = new SchemaManager(loggerFactory);
        _storeOptions = storeOptions;
        _ownsClient = ownsClient;
        _namespace = storeOptions?.Namespace ?? "test";
        _database = storeOptions?.Database ?? "test";
        _runtimeIdentity = runtimeIdentity ?? new AeroDBRuntimeNodeIdentity();

        // Derive Uri from the actual connection endpoint if available
        Uri = storeIdentity ?? (storeOptions?.Endpoint is { Length: > 0 } ep
            ? new Uri(ep)
            : new Uri("AeroDB://localhost"));
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
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Options.Durability.MessageIdentity != MessageIdentity.IdOnly)
        {
            throw new InvalidOperationException(
                "AeroDBMessageStore supports Wolverine MessageIdentity.IdOnly only. " +
                "MessageIdentity.IdAndDestination requires composite incoming-envelope identity support.");
        }

        _runtimeIdentity.BindRuntime(runtime);
        Name = runtime.Options.ServiceName ?? "AeroDB.Sable";
        _logger.LogInformation("AeroDBMessageStore initialized for Wolverine runtime {NodeId}", runtime.Options.UniqueNodeId);
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
        // Wolverine performs the explicit current-owner release as a separate
        // lifecycle step. This store has no buffered work to drain, and must
        // never reset ownership held by live peers here.
        await Task.CompletedTask;
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        // Return a polling agent that checks for ready scheduled messages
        // and moves them to 'Incoming' status for the durability agent.
        return new AeroDBScheduledJobAgent(Client, runtime.Logger);
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        ArgumentNullException.ThrowIfNull(listenerAddress);
        var sql = $"SELECT * FROM {IncomingTable} WHERE owner_id = 0 AND status = 'Incoming' AND received_at = $p0 ORDER BY execution_time ASC LIMIT {limit}";
        var response = await Client.RawQuery(
            sql,
            new Dictionary<string, object?> { ["p0"] = listenerAddress.ToString() });
        response.EnsureAllOks();
        var records = DeserializeIncomingEnvelopes(response, 0);
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
            var response = await Client.RawQuery(
                $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET owner_id = {ownerId};");
            response.EnsureAllOks();
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
    internal int GetOwnerId() => _runtimeIdentity.GetOwnerId();

    /// <summary>
    /// Indicates whether this store releases its client during disposal. Tenant
    /// store pairs use this internal discriminator to enforce single ownership.
    /// </summary>
    internal bool OwnsClient => _ownsClient;

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeOwnedClientAsync();
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeOwnedClientAsync()
    {
        _hasDisposed = true;
        if (!_ownsClient) return;

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
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Incoming', owner_id = 0, attempts = {envelope.Attempts}, execution_time = <datetime>'{execTimeStr}'");
        response.EnsureAllOks();
    }

    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        var execTime = envelope.ScheduledTime ?? DateTimeOffset.UtcNow;
        var execTimeStr = execTime.ToString("o");
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET status = 'Scheduled', execution_time = <datetime>'{execTimeStr}'");
        response.EnsureAllOks();
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        if (_hasDisposed) return;

        var env = AeroDBEnvelope.FromEnvelope(envelope, GetOwnerId());
        env.Status = EnvelopeStatus.Incoming.ToString();

        var deadId = envelope.Id.ToString();

        var json = JsonSerializer.Serialize(env, JsonOptions);

        var sql = $"BEGIN TRANSACTION;" +
                  $"DELETE FROM {IncomingTable}:`{EscapeId(deadId)}`;" +
                  $"CREATE {DeadLetterTable} CONTENT {json};" +
                  $"COMMIT TRANSACTION;";
        var response = await Client.RawQuery(sql);
        response.EnsureAllOks();
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable}:`{EscapeId(id)}` SET attempts = {envelope.Attempts}");
        response.EnsureAllOks();
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var command = BuildIncomingCreateCommand(envelope, GetOwnerId());
        var response = await Client.RawQuery(command.Sql, command.Parameters);
        if (IsDuplicateIncomingResponse(response))
            throw new DuplicateIncomingEnvelopeException([envelope]);

        response.EnsureAllOks();
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (_hasDisposed || envelopes.Count == 0) return;

        var repeatedInBatch = envelopes
            .GroupBy(envelope => envelope.Id)
            .SelectMany(group => group.Skip(1))
            .ToArray();
        if (repeatedInBatch.Length > 0)
            throw new ArgumentException(
                "A durable incoming-envelope batch cannot contain the same envelope ID more than once.",
                nameof(envelopes));

        var existing = await FindExistingIncomingAsync(envelopes).ConfigureAwait(false);
        if (existing.Count > 0)
            throw new DuplicateIncomingEnvelopeException(existing);

        var sql = new StringBuilder();
        var parameters = new Dictionary<string, object?>(envelopes.Count * 2, StringComparer.Ordinal);
        for (var index = 0; index < envelopes.Count; index++)
        {
            var command = BuildIncomingCreateCommand(envelopes[index], GetOwnerId());
            var idParameter = $"id{index}";
            var contentParameter = $"content{index}";
            parameters[idParameter] = command.Values[0];
            parameters[contentParameter] = command.Values[1];
            sql.Append($"CREATE type::record(\"{IncomingTable}\", ${idParameter}) CONTENT ${contentParameter}; ");
        }

        await using var session = await CreateScopedSessionAsync().ConfigureAwait(false);
        await using var transaction = await session.BeginTransaction().ConfigureAwait(false);
        var response = await transaction.RawQuery(sql.ToString(), parameters).ConfigureAwait(false);

        if (IsDuplicateIncomingResponse(response))
        {
            await transaction.Cancel().ConfigureAwait(false);
            var duplicates = await FindExistingIncomingAsync(envelopes).ConfigureAwait(false);
            if (duplicates.Count > 0)
                throw new DuplicateIncomingEnvelopeException(duplicates);
        }

        response.EnsureAllOks();
        await transaction.Commit().ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_hasDisposed, this);
        cancellation.ThrowIfCancellationRequested();
        var id = envelope.Id.ToString();
        var response = await Client.RawQuery(
            $"SELECT * FROM {IncomingTable}:`{EscapeId(id)}`",
            cancellationToken: cancellation);
        response.EnsureAllOks();
        var records = response.GetValue<List<WolverineIncomingEnvelope>>(0)
            ?? throw new InvalidOperationException("The incoming envelope existence query did not return a record collection.");
        return records.Count > 0;
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var command = BuildMarkIncomingHandledCommand(envelope, DateTimeOffset.UtcNow.AddDays(1));
        var response = await Client.RawQuery(command.Sql, command.Parameters);
        response.EnsureAllOks();
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (_hasDisposed || envelopes.Count == 0) return;

        var keepUntil = DateTimeOffset.UtcNow.AddDays(1);
        var sql = new StringBuilder("BEGIN TRANSACTION;");
        var parameters = new Dictionary<string, object?>(envelopes.Count * 2, StringComparer.Ordinal);
        for (var index = 0; index < envelopes.Count; index++)
        {
            var envelope = envelopes[index];
            var idParameter = $"id{index}";
            var keepUntilParameter = $"keepUntil{index}";
            var updateVariable = $"updated{index}";
            parameters[idParameter] = envelope.Id.ToString();
            parameters[keepUntilParameter] = keepUntil;
            sql.Append($"LET ${updateVariable} = UPDATE type::record(\"{IncomingTable}\", ${idParameter}) SET status = 'Handled', keep_until = ${keepUntilParameter} RETURN AFTER; ");
            sql.Append($"IF array::len(${updateVariable}) = 0 {{ THROW 'Incoming envelope does not exist'; }}; ");
        }

        sql.Append("COMMIT TRANSACTION;");
        var response = await Client.RawQuery(sql.ToString(), parameters);
        response.EnsureAllOks();
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        if (_hasDisposed) return;
        var uriStr = EscapeSurql(receivedAt.ToString());
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0 WHERE owner_id = {ownerId} AND received_at = '{uriStr}'");
        response.EnsureAllOks();
    }

    // ─── IMessageOutbox ───

    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        var destStr = EscapeSurql(destination.ToString());
        var response = await Client.RawQuery(
            $"{OutgoingSelectSql} WHERE owner_id = 0 AND destination = '{destStr}' ORDER BY execution_time ASC");
        response.EnsureAllOks();
        var records = DeserializeOutgoingEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        if (_hasDisposed) return;
        var command = BuildOutgoingCreateCommand(envelope, ownerId);
        var response = await Client.RawQuery(command.Sql, command.Parameters);
        response.EnsureAllOks();
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
        var response = await Client.RawQuery(sb.ToString());
        response.EnsureAllOks();
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        if (_hasDisposed) return;
        var id = envelope.Id.ToString();
        var response = await Client.RawQuery($"DELETE FROM {OutgoingTable}:`{EscapeId(id)}`");
        response.EnsureAllOks();
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
        var response = await Client.RawQuery(sb.ToString());
        response.EnsureAllOks();
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

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"DELETE FROM {NodesTable}; DELETE FROM {AgentRestrictionsTable}; DELETE FROM {NodeRecordsTable};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
    }

    public async Task<int> PersistAsync(Wolverine.Runtime.Agents.WolverineNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        _runtimeIdentity.ValidateNode(node);

        // Never accept Wolverine's provisional Balanced-mode owner (normally 1).
        // The counter and the node record are created in one database transaction.
        // Repeating a completed call for the same node reads and returns its original
        // allocation; failures with an unknown commit outcome are deliberately not retried.
        var nodeContent = new Dictionary<string, object?>
        {
            ["description"] = node.Description,
            ["control_uri"] = node.ControlUri?.ToString(),
            ["capabilities"] = node.Capabilities.Select(u => u.ToString()).ToList(),
            ["assigned_agents"] = node.ActiveAgents.Select(u => u.ToString()).ToList(),
            ["health_check_time"] = node.LastHealthCheck,
            ["started"] = node.Started,
            ["version"] = node.Version?.ToString()
        };

        var nodeId = EscapeId(node.NodeId.ToString());
        SurrealDbResponse response;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await Client.RawQuery(
                $"BEGIN TRANSACTION; " +
            $"LET $existing = (SELECT * FROM {NodesTable}:`{nodeId}`)[0]; " +
            $"IF $existing = NONE {{ " +
            $"LET $numbers = SELECT VALUE node_number FROM {NodesTable} WHERE node_number > 0; " +
            $"LET $max = IF array::len($numbers) = 0 THEN NONE ELSE math::max($numbers) END; " +
            $"UPSERT {NodeSequenceTable}:global SET next_node_number = IF next_node_number = NONE THEN (IF $max = NONE THEN 1 ELSE $max + 1 END) ELSE next_node_number END; " +
            $"LET $current = (SELECT * FROM {NodeSequenceTable}:global)[0]; " +
            $"IF $max != NONE AND $current.next_node_number <= $max {{ THROW 'Invalid Wolverine node sequence state'; }}; " +
            $"LET $sequence = (UPDATE {NodeSequenceTable}:global SET next_node_number += 1 RETURN BEFORE)[0]; " +
            $"LET $allocated = $sequence.next_node_number; " +
            $"IF $allocated <= 0 OR $allocated > 2147483647 {{ THROW 'Wolverine node sequence exceeds supported owner range'; }}; " +
            $"CREATE {NodesTable}:`{nodeId}` CONTENT object::extend($nodeContent, {{ node_number: $allocated }}); " +
            $"}}; " +
            $"COMMIT TRANSACTION;",
                    new Dictionary<string, object?> { ["nodeContent"] = nodeContent },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (attempt < 3 && IsConfirmedAllocationRollbackConflict(response))
                    continue;
                response.EnsureAllOks();
                break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception originalFailure)
        {
            // A transport failure after commit is ambiguous. Reconcile exactly
            // this immutable Wolverine node ID once; never allocate again.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reconciled = await LoadNodeStrictAsync(node.NodeId, cancellationToken).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    _runtimeIdentity.RecordPersistedNode(node, reconciled.AssignedNodeNumber);
                    return reconciled.AssignedNodeNumber;
                }
            }
            catch
            {
                // The original failure remains the most accurate outcome.
            }

            ExceptionDispatchInfo.Capture(originalFailure).Throw();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var persisted = await LoadNodeStrictAsync(node.NodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The persisted Wolverine node could not be read back.");
        if (persisted.AssignedNodeNumber <= 0)
            throw new InvalidOperationException("The persisted Wolverine node has an invalid database-assigned owner number.");

        _runtimeIdentity.RecordPersistedNode(node, persisted.AssignedNodeNumber);
        return persisted.AssignedNodeNumber;
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        if (assignedNodeNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(assignedNodeNumber));

        var response = await Client.RawQuery(
            $"BEGIN TRANSACTION; " +
            $"LET $removed = DELETE FROM {NodesTable}:`{EscapeId(nodeId.ToString())}` WHERE node_number = $nodeNumber RETURN BEFORE; " +
            "IF array::len($removed) = 0 { THROW 'Wolverine node identity and owner did not match'; }; " +
            $"UPDATE {IncomingTable} SET owner_id = 0 WHERE owner_id = $nodeNumber; " +
            $"UPDATE {OutgoingTable} SET owner_id = 0 WHERE owner_id = $nodeNumber; " +
            "COMMIT TRANSACTION;",
            new Dictionary<string, object?> { ["nodeNumber"] = assignedNodeNumber });
        response.EnsureAllOks();
    }

    public async Task<IReadOnlyList<Wolverine.Runtime.Agents.WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"SELECT * FROM {NodesTable} ORDER BY node_number ASC",
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        return DeserializeNodeListStrict(response);
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken ct)
    {
        if (_hasDisposed) return;
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder("BEGIN TRANSACTION;");
        var parameters = new Dictionary<string, object?>();
        sb.AppendLine($"DELETE FROM {AgentRestrictionsTable};");

        for (var index = 0; index < restrictions.Count; index++)
        {
            var restriction = restrictions[index];
            var idName = $"restrictionId{index}";
            var contentName = $"restrictionContent{index}";
            parameters[idName] = restriction.Id.ToString();
            parameters[contentName] = new Dictionary<string, object?>
            {
                ["agent_uri"] = restriction.AgentUri.ToString(),
                ["type"] = restriction.Type.ToString(),
                ["node_number"] = restriction.NodeNumber
            };
            sb.AppendLine($"CREATE type::record(\"{AgentRestrictionsTable}\", ${idName}) CONTENT ${contentName};");
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        var response = await Client.RawQuery(sb.ToString(), parameters, ct).ConfigureAwait(false);
        response.EnsureAllOks();
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = await LoadAllNodesAsync(cancellationToken);
        var response = await Client.RawQuery($"SELECT * FROM {AgentRestrictionsTable}", cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
        var persisted = response.GetValue<List<WolverineAgentRestrictions>>(0)
            ?? throw new InvalidOperationException("The node restriction response was missing its result set.");
        var restrictions = new List<AgentRestriction>(persisted.Count);
        foreach (var restriction in persisted)
        {
            if (restriction.Id is null || !restriction.Id.TryDeserializeId<string>(out var idText) || !Guid.TryParse(idText, out var id)
                || !Uri.TryCreate(restriction.AgentUri, UriKind.Absolute, out var uri)
                || !Enum.TryParse<AgentRestrictionType>(restriction.Type, out var type)
                || !Enum.IsDefined(type)
                || (type == AgentRestrictionType.Pinned && restriction.NodeNumber <= 0)
                || (type is AgentRestrictionType.Paused or AgentRestrictionType.None && restriction.NodeNumber != 0))
                throw new InvalidOperationException("The node restriction response contained an invalid record.");
            restrictions.Add(new AgentRestriction(id, uri, type, restriction.NodeNumber));
        }

        return new NodeAgentState(nodes, new AgentRestrictions(restrictions.ToArray()));
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"LET $updated = UPDATE {NodesTable}:`{EscapeId(nodeId.ToString())}` SET assigned_agents = $agents RETURN AFTER; " +
            "IF array::len($updated) = 0 { THROW 'Wolverine node does not exist'; };",
            new Dictionary<string, object?> { ["agents"] = agents.Select(uri => uri.ToString()).ToArray() }, ct).ConfigureAwait(false);
        response.EnsureAllOks();
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = await LoadNodeAsync(nodeId, ct);
        if (node is null) throw new InvalidOperationException("Wolverine node does not exist.");
        node.ActiveAgents.Remove(agentUri);
        await AssignAgentsAsync(nodeId, node.ActiveAgents, ct).ConfigureAwait(false);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = await LoadNodeAsync(nodeId, ct);
        if (node is null) throw new InvalidOperationException("Wolverine node does not exist.");
        if (!node.ActiveAgents.Contains(agentUri))
            node.ActiveAgents.Add(agentUri);
        await AssignAgentsAsync(nodeId, node.ActiveAgents, ct).ConfigureAwait(false);
    }

    public async Task<Wolverine.Runtime.Agents.WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"SELECT * FROM {NodesTable}:`{EscapeId(nodeId.ToString())}`",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
        var nodes = DeserializeNodeListStrict(response);
        if (nodes.Count > 1)
            throw new InvalidOperationException("A Wolverine node identity resolved to more than one persisted record.");
        return nodes.SingleOrDefault();
    }

    private async Task<Wolverine.Runtime.Agents.WolverineNode?> LoadNodeStrictAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"SELECT * FROM {NodesTable}:`{EscapeId(nodeId.ToString())}`",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
        var nodes = DeserializeNodeListStrict(response);
        if (nodes.Count > 1)
            throw new InvalidOperationException("A Wolverine node identity resolved to more than one persisted record.");
        return nodes.SingleOrDefault();
    }

    private async Task RecreatePersistedNodeAsync(
        Wolverine.Runtime.Agents.WolverineNode node,
        int ownerId,
        CancellationToken cancellationToken)
    {
        var content = new Dictionary<string, object?>
        {
            ["node_number"] = ownerId,
            ["description"] = node.Description,
            ["control_uri"] = node.ControlUri?.ToString(),
            ["capabilities"] = node.Capabilities.Select(uri => uri.ToString()).ToList(),
            ["assigned_agents"] = node.ActiveAgents.Select(uri => uri.ToString()).ToList(),
            ["started"] = node.Started,
            ["version"] = node.Version?.ToString()
        };
        var response = await Client.RawQuery(
            $"CREATE {NodesTable}:`{EscapeId(node.NodeId.ToString())}` CONTENT object::extend($content, {{ health_check_time: time::now() }})",
            new Dictionary<string, object?> { ["content"] = content },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
    }

    public async Task MarkHealthCheckAsync(Wolverine.Runtime.Agents.WolverineNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        if (node.NodeId != _runtimeIdentity.GetRequiredNodeId())
            throw new InvalidOperationException("AeroDB can only mark health for its persisted runtime node.");
        var ownerId = GetOwnerId();
        var response = await Client.RawQuery(
            $"UPDATE {NodesTable}:`{EscapeId(node.NodeId.ToString())}` SET health_check_time = time::now() WHERE node_number = $nodeNumber RETURN AFTER",
            new Dictionary<string, object?> { ["nodeNumber"] = ownerId },
            cancellationToken: cancellationToken);
        response.EnsureAllOks();
        var updated = response.GetValue<List<WolverineNodeAuthorityRecord>>(0)
            ?? throw new InvalidOperationException("The Wolverine node heartbeat did not return a result set.");
        if (updated.Count > 1)
            throw new InvalidOperationException("The Wolverine node heartbeat updated more than one record.");
        if (updated.Count == 1 && updated[0].NodeNumber != ownerId)
            throw new InvalidOperationException("The Wolverine node heartbeat updated an unexpected owner.");
        if (updated.Count == 0)
            await RecreatePersistedNodeAsync(node, ownerId, cancellationToken).ConfigureAwait(false);
        var persisted = await LoadNodeStrictAsync(node.NodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Wolverine heartbeat did not persist its node.");
        node.LastHealthCheck = persisted.LastHealthCheck;
        if (HasLeadershipLock())
            await RenewLeadershipLeaseAsync(cancellationToken).ConfigureAwait(false);
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

    public bool HasLeadershipLock()
    {
        lock (_leadershipStateGate)
        {
            if (!_hasLeadershipLock || _leadershipDeadline is not long deadline)
                return false;
            if (Stopwatch.GetTimestamp() < deadline)
                return true;
            _hasLeadershipLock = false;
            _leadershipDeadline = null;
            return false;
        }
    }

    private void SetLeadershipState(bool hasLock, long? deadline)
    {
        lock (_leadershipStateGate)
        {
            _hasLeadershipLock = hasLock;
            _leadershipDeadline = deadline;
        }
    }

    public async Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var nodeId = _runtimeIdentity.GetRequiredNodeId();
        var timeout = _runtimeIdentity.GetLeaseTimeout();
        var requestStarted = Stopwatch.GetTimestamp();
        await _leadershipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // The database clock is authoritative. A local deadline is only a
            // conservative cache: slow responses never extend local authority.
            var response = await Client.RawQuery(
                $"UPSERT {LeadershipLeaseTable}:global " +
                "SET owner_node_id = $nodeId, expires_at = time::now() + $ttl " +
                "WHERE owner_node_id = NONE OR owner_node_id = $nodeId OR expires_at <= time::now() RETURN AFTER;",
                new Dictionary<string, object?>
                {
                    ["nodeId"] = nodeId.ToString(),
                    ["ttl"] = timeout
                }, token).ConfigureAwait(false);
            response.EnsureAllOks();
            var deadline = DeadlineFrom(requestStarted, timeout);
            var won = Stopwatch.GetTimestamp() < deadline && LeaseBelongsTo(response, nodeId);
            SetLeadershipState(won, won ? deadline : null);
            return won;
        }
        catch (OperationCanceledException)
        {
            SetLeadershipState(false, null);
            throw;
        }
        catch
        {
            SetLeadershipState(false, null);
            return false;
        }
        finally { _leadershipGate.Release(); }
    }

    public async Task ReleaseLeadershipLockAsync()
    {
        Guid nodeId;
        try { nodeId = _runtimeIdentity.GetRequiredNodeId(); }
        catch { SetLeadershipState(false, null); return; }

        await _leadershipGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetLeadershipState(false, null);
            var response = await Client.RawQuery(
                $"DELETE FROM {LeadershipLeaseTable}:global WHERE owner_node_id = $nodeId",
                new Dictionary<string, object?> { ["nodeId"] = nodeId.ToString() }).ConfigureAwait(false);
            response.EnsureAllOks();
        }
        finally
        {
            SetLeadershipState(false, null);
            _leadershipGate.Release();
        }
    }

    private async Task RenewLeadershipLeaseAsync(CancellationToken cancellationToken)
    {
        var nodeId = _runtimeIdentity.GetRequiredNodeId();
        var timeout = _runtimeIdentity.GetLeaseTimeout();
        var requestStarted = Stopwatch.GetTimestamp();
        await _leadershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await Client.RawQuery(
                $"UPDATE {LeadershipLeaseTable}:global SET expires_at = time::now() + $ttl " +
                "WHERE owner_node_id = $nodeId RETURN AFTER;",
                new Dictionary<string, object?> { ["nodeId"] = nodeId.ToString(), ["ttl"] = timeout },
                cancellationToken).ConfigureAwait(false);
            response.EnsureAllOks();
            var deadline = DeadlineFrom(requestStarted, timeout);
            var renewed = Stopwatch.GetTimestamp() < deadline && LeaseBelongsTo(response, nodeId);
            SetLeadershipState(renewed, renewed ? deadline : null);
        }
        catch (OperationCanceledException)
        {
            SetLeadershipState(false, null);
            throw;
        }
        catch
        {
            SetLeadershipState(false, null);
            throw;
        }
        finally { _leadershipGate.Release(); }
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
        response.EnsureAllOks();
        var records = DeserializeIncomingEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        var response = await Client.RawQuery(OutgoingSelectSql);
        response.EnsureAllOks();
        var records = DeserializeOutgoingEnvelopes(response, 0);
        return records.Select(r => r.ToEnvelope()).ToList();
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0; UPDATE {OutgoingTable} SET owner_id = 0;");
        response.EnsureAllOks();
    }

    public Task ReleaseAllOwnershipAsync(int ownerId) =>
        ReleaseAllOwnershipAsync(ownerId, CancellationToken.None);

    internal async Task ReleaseAllOwnershipAsync(int ownerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"UPDATE {IncomingTable} SET owner_id = 0 WHERE owner_id = {ownerId}; UPDATE {OutgoingTable} SET owner_id = 0 WHERE owner_id = {ownerId};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureAllOks();
    }

    /// <summary>
    /// Returns only globally-owned incoming work grouped by listener address.
    /// This is strict recovery metadata: unsuccessful responses and malformed
    /// addresses are errors, never an empty result.
    /// </summary>
    internal async Task<IReadOnlyList<IncomingCount>> FindGloballyOwnedIncomingCountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"SELECT received_at, count() AS total FROM {IncomingTable} " +
            "WHERE owner_id = 0 AND status = 'Incoming' GROUP BY received_at;",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        response.EnsureAllOks();
        var rows = response.GetValue<List<IncomingCountProjection>>(0)
            ?? throw new InvalidOperationException("Incoming recovery metadata did not return a row collection.");

        var results = new List<IncomingCount>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null || !Uri.TryCreate(row.ReceivedAt, UriKind.Absolute, out var destination))
                throw new InvalidOperationException("Incoming recovery metadata contained an invalid listener address.");
            if (row.Total is not > 0 or > int.MaxValue)
                throw new InvalidOperationException("Incoming recovery metadata contained a missing or invalid count.");
            results.Add(new IncomingCount(destination, checked((int)row.Total.Value)));
        }

        return results;
    }

    /// <summary>
    /// Returns only globally-owned outgoing destinations. Response failures and
    /// malformed values are surfaced so recovery never silently skips work.
    /// </summary>
    internal async Task<IReadOnlyList<Uri>> FindGloballyOwnedOutgoingDestinationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await Client.RawQuery(
            $"SELECT destination FROM {OutgoingTable} WHERE owner_id = 0 GROUP BY destination;",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        response.EnsureAllOks();
        var rows = response.GetValue<List<OutgoingDestinationProjection>>(0)
            ?? throw new InvalidOperationException("Outgoing recovery metadata did not return a row collection.");

        var destinations = new List<Uri>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null || !Uri.TryCreate(row.Destination, UriKind.Absolute, out var destination))
                throw new InvalidOperationException("Outgoing recovery metadata contained an invalid destination address.");
            destinations.Add(destination);
        }

        return destinations;
    }

    /// <summary>
    /// Finds owner ids across both durable tables. This is recovery metadata,
    /// so every statement response is checked before its values are trusted.
    /// </summary>
    internal async Task<IReadOnlySet<int>> FindDistinctOwnerIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incomingResponse = await Client.RawQuery(
            $"SELECT owner_id FROM {IncomingTable} WHERE owner_id != 0 GROUP BY owner_id;",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var outgoingResponse = await Client.RawQuery(
            $"SELECT owner_id FROM {OutgoingTable} WHERE owner_id != 0 GROUP BY owner_id;",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Do not return a partial owner set: both metadata reads must succeed
        // before either result can be trusted for ownership release.
        incomingResponse.EnsureAllOks();
        outgoingResponse.EnsureAllOks();
        var owners = new HashSet<int>();
        foreach (var response in new[] { incomingResponse, outgoingResponse })
        {
            var rows = response.GetValue<List<OwnerIdProjection>>(0)
                ?? throw new InvalidOperationException("Owner recovery metadata did not return a row collection.");
            foreach (var row in rows)
            {
                if (row.OwnerId is not > 0)
                    throw new InvalidOperationException("Owner recovery metadata contained a missing or invalid owner id.");
                owners.Add(row.OwnerId.Value);
            }
        }

        return owners;
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
        var createSession = _storeOptions?.Advanced.CreateSessionAsync;
        if (createSession is null && HasConfiguredCredentials(_storeOptions))
        {
            throw new InvalidOperationException(
                "A credentialed AeroDB message store requires the DocumentStore session factory. " +
                "Initialize the DocumentStore before initializing Wolverine durability.");
        }

        await using var session = createSession is null
            ? await Client.CreateSession().ConfigureAwait(false)
            : await createSession(CancellationToken.None).ConfigureAwait(false);

        if (createSession is null)
            await session.Use(_namespace, _database).ConfigureAwait(false);

        await _schemaManager.EnsureDocumentSchemaAsync<WolverineIncomingEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineOutgoingEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineDeadLetterEnvelope>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineNode>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineNodeSequence>(session, SchemaMode.Strict).ConfigureAwait(false);
        await _schemaManager.EnsureDocumentSchemaAsync<WolverineLeadershipLease>(session, SchemaMode.Strict).ConfigureAwait(false);
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
        await _schemaManager.EnsureIndexAsync(session, NodesTable, new IndexDefinition
        {
            Name = "idx_nodes_node_number", Columns = ["node_number"], IsUnique = true, Type = IndexType.Standard
        }).ConfigureAwait(false);

        _logger.LogInformation("AeroDBMessageStore schema initialized (via SchemaManager pipeline)");
    }

    private static bool HasConfiguredCredentials(StoreOptions? storeOptions) =>
        storeOptions is not null &&
        (!string.IsNullOrWhiteSpace(storeOptions.Username) ||
         !string.IsNullOrWhiteSpace(storeOptions.Password) ||
         !string.IsNullOrWhiteSpace(storeOptions.Token));

    private async Task<ISurrealDbSession> CreateScopedSessionAsync()
    {
        var createSession = _storeOptions?.Advanced.CreateSessionAsync;
        if (createSession is not null)
            return await createSession(CancellationToken.None).ConfigureAwait(false);

        if (HasConfiguredCredentials(_storeOptions))
        {
            throw new InvalidOperationException(
                "A credentialed AeroDB message store requires the DocumentStore session factory. " +
                "Initialize the DocumentStore before using Wolverine durability.");
        }

        var session = await Client.CreateSession().ConfigureAwait(false);
        try
        {
            await session.Use(_namespace, _database).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<Envelope>> FindExistingIncomingAsync(IEnumerable<Envelope> envelopes)
    {
        var existing = new List<Envelope>();
        foreach (var envelope in envelopes)
        {
            if (await ExistsAsync(envelope, CancellationToken.None).ConfigureAwait(false))
                existing.Add(envelope);
        }

        return existing;
    }

    private static bool IsDuplicateIncomingResponse(SurrealDbResponse response)
    {
        var errors = response.Errors.ToArray();
        return errors.Length > 0 && errors.All(error =>
            error is SurrealDbErrorResult { Kind: RpcErrorKind.AlreadyExists });
    }

    private static bool LeaseBelongsTo(SurrealDbResponse response, Guid nodeId)
    {
        var rows = response.GetValue<List<WolverineLeadershipLease>>(0);
        return rows is { Count: 1 }
            && string.Equals(rows[0].OwnerNodeId, nodeId.ToString(), StringComparison.Ordinal)
            && rows[0].ExpiresAt is not null;
    }

    private static bool IsConfirmedAllocationRollbackConflict(SurrealDbResponse response)
    {
        var errors = response.Errors.ToArray();
        if (errors.Any(error => error is not SurrealDbErrorResult))
            return false;
        return IsConfirmedAllocationRollbackConflict(errors.Cast<SurrealDbErrorResult>()
            .Select(error => (error.Kind, (string?)error.Details)).ToArray());
    }

    internal static bool IsConfirmedAllocationRollbackConflict(
        IReadOnlyList<(RpcErrorKind Kind, string? Details)> errors)
    {
        const string commitConflict = "Cannot COMMIT: Transaction conflict: Write conflict, retry the transaction. This transaction can be retried";
        const string notExecuted = "The query was not executed due to a failed transaction";
        if (errors.Count < 2 || errors.Any(error => error.Kind != RpcErrorKind.Query))
            return false;
        var details = errors.Select(error => error.Details).ToArray();
        return details.Count(detail => string.Equals(detail, commitConflict, StringComparison.Ordinal)) == 1
            && details.Where(detail => !string.Equals(detail, commitConflict, StringComparison.Ordinal))
                .All(detail => string.Equals(detail, notExecuted, StringComparison.Ordinal));
    }

    private static long DeadlineFrom(long requestStarted, TimeSpan timeout)
    {
        var elapsedTicks = checked((long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency));
        return checked(requestStarted + elapsedTicks);
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

    // Authority decisions cannot tolerate the legacy best-effort node reader:
    // one corrupt row must not turn a partial list into permission to reclaim
    // ownership from a healthy node.
    private List<Wolverine.Runtime.Agents.WolverineNode> DeserializeNodeListStrict(
        SurrealDb.Net.Models.Response.SurrealDbResponse response)
    {
        // Do not route this authority decision through DeserializeResponse: that
        // legacy helper suppresses driver deserialization failures and would turn
        // malformed authority data into a misleading missing result.
        var raw = response.GetValue<List<WolverineNodeAuthorityRecord>>(0)
            ?? throw new InvalidOperationException("The node authority response was missing its result set.");
        var nodes = new List<Wolverine.Runtime.Agents.WolverineNode>(raw.Count);
        foreach (var row in raw)
        {
            if (row.Id is null
                || !row.Id.TryDeserializeId<string>(out var idText)
                || !Guid.TryParse(idText, out var nodeId)
                || row.NodeNumber <= 0)
                throw new InvalidOperationException("The node authority response contained an invalid node.");

            var node = new Wolverine.Runtime.Agents.WolverineNode
            {
                NodeId = nodeId,
                AssignedNodeNumber = row.NodeNumber,
                Description = row.Description ?? string.Empty
            };

            if (row.Started is not null)
                node.Started = row.Started.Value;

            if (row.HealthCheckTime is null)
                throw new InvalidOperationException("The node authority response contained a missing health_check_time.");
            node.LastHealthCheck = row.HealthCheckTime.Value.ToUniversalTime();

            if (row.ControlUri is not null)
                node.ControlUri = ParseAuthorityUri(row.ControlUri, "control_uri");

            if (row.Version is not null)
                node.Version = ParseAuthorityVersion(row.Version);

            AddAuthorityUris(row.Capabilities, node.Capabilities, "capabilities");
            AddAuthorityUris(row.AssignedAgents, node.ActiveAgents, "assigned_agents");
            nodes.Add(node);
        }

        if (nodes.Select(node => node.AssignedNodeNumber).Distinct().Count() != nodes.Count)
            throw new InvalidOperationException("The node authority response contained duplicate node owners.");

        return nodes;
    }

    private static Uri ParseAuthorityUri(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"The node authority response contained an invalid {field} value.");

        return uri;
    }

    private static Version ParseAuthorityVersion(string value)
    {
        if (!Version.TryParse(value, out var version))
            throw new InvalidOperationException("The node authority response contained an invalid version value.");

        return version;
    }

    private static void AddAuthorityUris(
        IEnumerable<string>? values,
        ICollection<Uri> destination,
        string field)
    {
        if (values is null)
            return;

        foreach (var value in values)
            destination.Add(ParseAuthorityUri(value, field));
    }

    /// <summary>
    /// Deserialize SurrealDB response records into AeroDBEnvelope objects.
    /// Uses Dictionary-based deserialization to avoid CBOR issues with
    /// the Record base class's RecordId? Id conflicting with the string Id.
    /// </summary>
    private static List<AeroDBEnvelope> DeserializeAeroDBEnvelopes(
        SurrealDb.Net.Models.Response.SurrealDbResponse response,
        int index,
        string? fallbackStatus = null)
    {
        try
        {
            var raw = response.GetValue<List<Dictionary<string, object>>>(index);
            if (raw is null) return [];

            return raw.Select(row => MapToAeroDBEnvelope(row, fallbackStatus)).Where(e => e is not null).ToList()!;
        }
        catch
        {
            return [];
        }
    }

    internal EnvelopeSqlCommand BuildIncomingCreateCommand(Envelope envelope, int ownerId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var persisted = AeroDBEnvelope.FromEnvelope(envelope, ownerId);
        persisted.ReceivedAt = envelope.Destination?.ToString();
        return BuildCreateCommand(IncomingTable, persisted.Id, ToIncomingContent(persisted));
    }

    private static List<AeroDBEnvelope> DeserializeOutgoingEnvelopes(
        SurrealDb.Net.Models.Response.SurrealDbResponse response,
        int index)
    {
        var records = response.GetValue<List<WolverineOutgoingEnvelope>>(index)
            ?? throw new InvalidOperationException("The outgoing envelope query did not return a record collection.");
        return records.Select(record => new AeroDBEnvelope
        {
            Id = RequireEnvelopeId(record.Id),
            Status = EnvelopeStatus.Outgoing.ToString(),
            OwnerId = record.OwnerId,
            ExecutionTime = record.ExecutionTime,
            Attempts = record.Attempts,
            Body = RequireBase64Body(record.Body),
            MessageType = RequireMessageType(record.MessageType),
            Destination = record.Destination,
            DeliverBy = record.DeliverBy,
            CorrelationId = record.CorrelationId,
            Source = record.Source,
            TenantId = record.TenantId,
            KeepUntil = record.KeepUntil,
            ContentType = record.ContentType,
            ReplyUri = record.ReplyUri,
            SagaId = record.SagaId,
            ConversationId = record.ConversationId
        }).ToList();
    }

    private static List<AeroDBEnvelope> DeserializeIncomingEnvelopes(
        SurrealDb.Net.Models.Response.SurrealDbResponse response,
        int index)
    {
        var records = response.GetValue<List<WolverineIncomingEnvelope>>(index)
            ?? throw new InvalidOperationException("The incoming envelope query did not return a record collection.");
        return records.Select(record => new AeroDBEnvelope
        {
            Id = RequireEnvelopeId(record.Id),
            Status = RequireEnvelopeStatus(record.Status),
            OwnerId = record.OwnerId,
            ExecutionTime = record.ExecutionTime,
            Attempts = record.Attempts,
            Body = RequireBase64Body(record.Body),
            MessageType = RequireMessageType(record.MessageType),
            Destination = record.Destination,
            DeliverBy = record.DeliverBy,
            CorrelationId = record.CorrelationId,
            Source = record.Source,
            TenantId = record.TenantId,
            KeepUntil = record.KeepUntil,
            ContentType = record.ContentType,
            ReceivedAt = record.ReceivedAt,
            ReplyUri = record.ReplyUri,
            SagaId = record.SagaId,
            ConversationId = record.ConversationId
        }).ToList();
    }

    internal EnvelopeSqlCommand BuildOutgoingCreateCommand(Envelope envelope, int ownerId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var persisted = AeroDBEnvelope.FromOutgoingEnvelope(envelope, ownerId);
        return BuildCreateCommand(OutgoingTable, persisted.Id, ToOutgoingContent(persisted));
    }

    internal EnvelopeSqlCommand BuildMarkIncomingHandledCommand(Envelope envelope, DateTimeOffset keepUntil)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var values = new object[] { envelope.Id.ToString(), keepUntil };
        return new EnvelopeSqlCommand(
            $"LET $updated = UPDATE type::record(\"{IncomingTable}\", $p0) SET status = 'Handled', keep_until = $p1 RETURN AFTER; " +
            "IF array::len($updated) = 0 { THROW 'Incoming envelope does not exist'; };",
            ToParameters(values),
            values);
    }

    private static EnvelopeSqlCommand BuildCreateCommand(
        string table,
        string envelopeId,
        IReadOnlyDictionary<string, object?> content)
    {
        var values = new object[] { envelopeId, content };
        return new EnvelopeSqlCommand(
            $"CREATE type::record(\"{table}\", $p0) CONTENT $p1;",
            ToParameters(values),
            values);
    }

    private static IReadOnlyDictionary<string, object?> ToParameters(IReadOnlyList<object> values)
    {
        var parameters = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
            parameters[$"p{index}"] = values[index];
        return parameters;
    }

    private static IReadOnlyDictionary<string, object?> ToIncomingContent(AeroDBEnvelope envelope) =>
        OmitNullValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = envelope.Status,
            ["owner_id"] = envelope.OwnerId,
            ["execution_time"] = envelope.ExecutionTime,
            ["attempts"] = envelope.Attempts,
            ["body"] = envelope.Body,
            ["message_type"] = envelope.MessageType,
            ["destination"] = envelope.Destination,
            ["deliver_by"] = envelope.DeliverBy,
            ["correlation_id"] = envelope.CorrelationId,
            ["source"] = envelope.Source,
            ["tenant_id"] = envelope.TenantId,
            ["keep_until"] = envelope.KeepUntil,
            ["content_type"] = envelope.ContentType,
            ["received_at"] = envelope.ReceivedAt,
            ["reply_uri"] = envelope.ReplyUri,
            ["saga_id"] = envelope.SagaId,
            ["conversation_id"] = envelope.ConversationId
        });

    private static IReadOnlyDictionary<string, object?> ToOutgoingContent(AeroDBEnvelope envelope) =>
        OmitNullValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner_id"] = envelope.OwnerId,
            ["execution_time"] = envelope.ExecutionTime,
            ["attempts"] = envelope.Attempts,
            ["body"] = envelope.Body,
            ["message_type"] = envelope.MessageType,
            ["destination"] = envelope.Destination,
            ["deliver_by"] = envelope.DeliverBy,
            ["correlation_id"] = envelope.CorrelationId,
            ["source"] = envelope.Source,
            ["tenant_id"] = envelope.TenantId,
            ["keep_until"] = envelope.KeepUntil,
            ["content_type"] = envelope.ContentType,
            ["reply_uri"] = envelope.ReplyUri,
            ["saga_id"] = envelope.SagaId,
            ["conversation_id"] = envelope.ConversationId
        });

    private static IReadOnlyDictionary<string, object?> OmitNullValues(
        Dictionary<string, object?> values)
    {
        foreach (var key in values.Where(entry => entry.Value is null).Select(entry => entry.Key).ToArray())
            values.Remove(key);

        return values;
    }

    private static AeroDBEnvelope? MapToAeroDBEnvelope(Dictionary<string, object> row, string? fallbackStatus)
    {
        try
        {
            return new AeroDBEnvelope
            {
                Id = NormalizeEnvelopeId(row.GetValueOrDefault("id")),
                Status = row.GetValueOrDefault("status")?.ToString() ?? fallbackStatus ?? string.Empty,
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

    private static string NormalizeEnvelopeId(object? value)
    {
        if (value is RecordId recordId && recordId.TryDeserializeId<string>(out var id))
            return id;

        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var separator = raw.LastIndexOf(':');
        var candidate = separator >= 0 ? raw[(separator + 1)..] : raw;
        return candidate.Trim('`', '⟨', '⟩', ' ');
    }

    private static string RequireEnvelopeId(object? value)
    {
        var id = NormalizeEnvelopeId(value);
        if (!Guid.TryParse(id, out _))
            throw new InvalidOperationException("A durable envelope record has an invalid identifier.");

        return id;
    }

    private static string RequireBase64Body(string? body)
    {
        if (body is null)
            throw new InvalidOperationException("A durable envelope record is missing its body.");

        _ = Convert.FromBase64String(body);
        return body;
    }

    private static string RequireEnvelopeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) ||
            !Enum.TryParse<EnvelopeStatus>(status, out var parsed) ||
            !Enum.IsDefined(parsed))
            throw new InvalidOperationException("A durable incoming envelope record has an invalid status.");

        return status;
    }

    private static string RequireMessageType(string? messageType)
    {
        if (string.IsNullOrWhiteSpace(messageType))
            throw new InvalidOperationException("A durable envelope record is missing its message type.");

        return messageType;
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

    private sealed class OwnerIdProjection
    {
        [Dahomey.Cbor.Attributes.CborProperty("owner_id")]
        public int? OwnerId { get; set; }
    }

    private sealed class IncomingCountProjection
    {
        [Dahomey.Cbor.Attributes.CborProperty("received_at")]
        public string? ReceivedAt { get; set; }

        [Dahomey.Cbor.Attributes.CborProperty("total")]
        public long? Total { get; set; }
    }

    private sealed class OutgoingDestinationProjection
    {
        [Dahomey.Cbor.Attributes.CborProperty("destination")]
        public string? Destination { get; set; }
    }
}

internal sealed record EnvelopeSqlCommand(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    object[] Values);
