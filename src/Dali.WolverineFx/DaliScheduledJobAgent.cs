using JasperFx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SurrealDb.Net;
using Wolverine.Runtime.Agents;

namespace Dali.WolverineFx;

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
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _batchWindow = TimeSpan.FromMinutes(1);

    private const string IncomingTable = "wolverine_incoming_envelopes";

    public Uri Uri { get; } = new("dali://scheduled-jobs");

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string Description => "AeroDB scheduled job agent: polls for ready scheduled messages";

    public DaliScheduledJobAgent(ISurrealDbClient client, ILogger logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ─── IHostedService ───

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
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
        _cts?.Dispose();
        _cts = null;
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
                await Task.Delay(_pollInterval).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                await MoveReadyScheduledMessagesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "DaliScheduledJobAgent poll error, will retry");
            }
        }
    }

    /// <summary>
    /// Moves scheduled messages that are due (execution_time &lt;= now) to 'Incoming' status.
    /// Uses string-based date comparison (ISO 8601 lexicographic orderable).
    /// </summary>
    private async Task MoveReadyScheduledMessagesAsync(CancellationToken ct)
    {
        var nowStr = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK");

        try
        {
            var updateSql =
                $"UPDATE {IncomingTable} SET status = 'Incoming', owner_id = 0 " +
                $"WHERE status = 'Scheduled' AND execution_time <= '{nowStr}'";

            await _client.RawQuery(updateSql, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error moving scheduled messages");
        }
    }
}
