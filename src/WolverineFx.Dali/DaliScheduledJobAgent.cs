using JasperFx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SurrealDb.Net;
using Wolverine.Runtime.Agents;

namespace WolverineFx.Dali;

/// <summary>
/// Polling agent that periodically checks for scheduled messages that are due
/// and moves them to 'Incoming' status so the durability agent can process them.
/// </summary>
internal sealed class DaliScheduledJobAgent : IAgent
{
    private readonly ISurrealDbClient _client;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _batchWindow = TimeSpan.FromMinutes(1);

    private const string IncomingTable = "wolverine_incoming_envelopes";

    public Uri Uri { get; } = new("dali://scheduled-jobs");

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string Description => "Dali scheduled job agent: polls for ready scheduled messages";

    public DaliScheduledJobAgent(ISurrealDbClient client, ILogger logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ─── IHostedService ───

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = PollLoopAsync(_cts.Token);
        Status = AgentStatus.Running;
        _logger.LogInformation("DaliScheduledJobAgent started, polling every {Interval}", _pollInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }
        Status = AgentStatus.Stopped;
        _logger.LogInformation("DaliScheduledJobAgent stopped");
    }

    // ─── IHealthCheck ───

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Status == AgentStatus.Running
            ? HealthCheckResult.Healthy("DaliScheduledJobAgent is running")
            : HealthCheckResult.Unhealthy($"DaliScheduledJobAgent is {Status}"));
    }

    // ─── Polling Loop ───

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                await MoveReadyScheduledMessagesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DaliScheduledJobAgent poll error, will retry");
            }
        }
    }

    /// <summary>
    /// Moves scheduled messages that are due (execution_time &lt;= now) to 'Incoming' status.
    /// </summary>
    private async Task MoveReadyScheduledMessagesAsync(CancellationToken ct)
    {
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        var windowEnd = DateTimeOffset.UtcNow.Add(_batchWindow).ToString("o");
        var sql = $@"
            UPDATE {IncomingTable}
            SET status = 'Incoming', owner_id = 0
            WHERE status = 'Scheduled'
              AND execution_time <= <datetime>'{nowStr}'
              AND execution_time >= <datetime>'{nowStr}'
        ";

        // Use a time-bounded window query to avoid scanning all future schedules.
        // Phase 1: identify ready rows using a SELECT
        var selectSql = $@"
            SELECT id FROM {IncomingTable}
            WHERE status = 'Scheduled'
              AND execution_time <= <datetime>'{nowStr}'
        ";

        try
        {
            var response = await _client.RawQuery(selectSql, cancellationToken: ct).ConfigureAwait(false);
            if (response.HasErrors || response.Count == 0) return;

            // Count how many we're moving
            var count = 0;
            if (response.FirstOk is not null)
            {
                try
                {
                    var raw = response.GetValue<List<Dictionary<string, object>>>(0);
                    if (raw is not null) count = raw.Count;
                }
                catch { /* best effort count */ }
            }

            if (count > 0)
            {
                // Move them to Incoming
                await _client.RawQuery(
                    $"UPDATE {IncomingTable} SET status = 'Incoming', owner_id = 0 WHERE status = 'Scheduled' AND execution_time <= <datetime>'{nowStr}'",
                    cancellationToken: ct).ConfigureAwait(false);
                _logger.LogDebug("Moved {Count} scheduled messages to Incoming", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error moving scheduled messages");
        }
    }
}
