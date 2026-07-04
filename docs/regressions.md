# Regressions Introduced During Phase 19

## `DaliScheduledJobAgent.cs` — Infrastructure Code Modifications

The original version (committed in `c9cbade`) was modified by a fixer during Phase 19 testing work. Two regressions were introduced:

### 1. CancellationTokenSource replaced with `volatile bool _stopping`

**Original (correct):**
```csharp
private CancellationTokenSource? _cts;

public Task StartAsync(CancellationToken cancellationToken)
{
    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _pollingTask = PollLoopAsync(_cts.Token);
    ...
}

private async Task PollLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        await MoveReadyScheduledMessagesAsync(ct).ConfigureAwait(false);
    }
}
```

**Current (regression):**
```csharp
private volatile bool _stopping;

public Task StartAsync(CancellationToken cancellationToken)
{
    _stopping = false;
    _pollingTask = PollLoopAsync();
    ...
}

private async Task PollLoopAsync()
{
    while (!_stopping)
    {
        await Task.Delay(_pollInterval).ConfigureAwait(false);
        if (_stopping) break;
        await MoveReadyScheduledMessagesAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
```

**Why the original is better:**
- `_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` links the polling loop to the lifecycle token, enabling coordinated shutdown
- `Task.Delay(_pollInterval, ct)` cancels immediately when stop is requested, instead of waiting for the delay duration to expire
- `MoveReadyScheduledMessagesAsync(ct)` passes the cancellation token to `RawQuery(..., cancellationToken: ct)`, enabling in-flight query cancellation
- No race condition between checking `_stopping` and entering `Task.Delay`

### 2. SELECT-then-UPDATE pattern simplified to bare UPDATE

**Original (correct):**
```csharp
private async Task MoveReadyScheduledMessagesAsync(CancellationToken ct)
{
    var nowStr = DateTimeOffset.UtcNow.ToString("o");

    var selectSql = $@"
        SELECT id FROM {IncomingTable}
        WHERE status = 'Scheduled'
          AND execution_time <= <datetime>'{nowStr}'
    ";

    var response = await _client.RawQuery(selectSql, cancellationToken: ct).ConfigureAwait(false);
    if (response.HasErrors || response.Count == 0) return;

    var count = ...; // parsed from response

    if (count > 0)
    {
        await _client.RawQuery(
            $"UPDATE {IncomingTable} SET status = 'Incoming', owner_id = 0 WHERE status = 'Scheduled' AND execution_time <= <datetime>'{nowStr}'",
            cancellationToken: ct).ConfigureAwait(false);
        _logger.LogDebug("Moved {Count} scheduled messages to Incoming", count);
    }
}
```

**Current (regression):**
```csharp
private async Task MoveReadyScheduledMessagesAsync(CancellationToken ct)
{
    var nowStr = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK");

    var updateSql =
        $"UPDATE {IncomingTable} SET status = 'Incoming', owner_id = 0 " +
        $"WHERE status = 'Scheduled' AND execution_time <= '{nowStr}'";

    await _client.RawQuery(updateSql, cancellationToken: ct).ConfigureAwait(false);
}
```

**What was lost:**
- **Logging**: No `LogDebug` of how many messages were moved — operational observability removed
- **Select-then-Update**: Original SELECT-first pattern avoided unnecessary UPDATEs on empty queues. Current version runs an UPDATE every poll cycle regardless
- **`<datetime>` cast**: Original used `<datetime>'{nowStr}'` which is engine-agnostic. Current uses bare string comparison (ISO 8601 lexicographic ordering), which relies on SurrealDB's implicit string comparison behavior and may behave differently across SurrealDB engine variants (KV, memory, cloud, embedded)
- **Error guard**: Original checked `response.HasErrors` and returned early. Current silently swallows all errors in the catch block
