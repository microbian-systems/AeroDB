using System.Reflection;
using AeroDB.Sable;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace AeroDB.Tests;

public class DaemonHealthCheckTests
{
    // ─── DaemonHealthState record tests ────────────────────────────────

    [Test]
    public void DaemonHealthState_default_is_not_running()
    {
        var state = new DaemonHealthState(false, null, null, 0, 0, null);

        state.IsRunning.ShouldBeFalse();
        state.LastSuccess.ShouldBeNull();
        state.LastError.ShouldBeNull();
        state.HighWaterSequence.ShouldBe(0);
        state.LagCount.ShouldBe(0);
        state.LastException.ShouldBeNull();
    }

    [Test]
    public void DaemonHealthState_record_equality()
    {
        var a = new DaemonHealthState(true, null, null, 100, 5, null);
        var b = new DaemonHealthState(true, null, null, 100, 5, null);

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
    }

    [Test]
    public void DaemonHealthState_with_clone_creates_new_state()
    {
        var state = new DaemonHealthState(true, null, null, 100, 5, null);
        var updated = state with { LastSuccess = DateTimeOffset.UtcNow };

        updated.IsRunning.ShouldBeTrue();
        updated.HighWaterSequence.ShouldBe(100);
        updated.LagCount.ShouldBe(5);
        updated.LastSuccess.ShouldNotBeNull();
        updated.LastError.ShouldBeNull();

        // Original should be unchanged
        state.LastSuccess.ShouldBeNull();
    }

    [Test]
    public void DaemonHealthState_with_exception_records_message()
    {
        var state = new DaemonHealthState(true, null, DateTimeOffset.UtcNow, 50, 3, "Connection failed");

        state.IsRunning.ShouldBeTrue();
        state.LastError.ShouldNotBeNull();
        state.LastException.ShouldBe("Connection failed");
        state.HighWaterSequence.ShouldBe(50);
    }

    // ─── AeroDBDaemonHealthCheck.CheckHealthAsync tests ─────────────────
    //
    // Branches covered:
    //   1. _daemon == null                          → Healthy("not configured")
    //   2. !health.IsRunning                        → Unhealthy("stopped")
    //   3. IsRunning + recent LastSuccess           → Healthy(description, data)
    //   4. IsRunning + stale/null LastSuccess       → Degraded("no recent success")

    [Test]
    public async Task HealthCheck_null_daemon_reports_healthy()
    {
        var check = new AeroDBDaemonHealthCheck(null, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description!.ShouldContain("not configured");
    }

    [Test]
    public async Task HealthCheck_stopped_daemon_reports_unhealthy()
    {
        var store = new DocumentStore(new StoreOptions());
        var daemon = new AsyncDaemon(store, []);
        var check = new AeroDBDaemonHealthCheck(daemon, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("stopped");
    }

    [Test]
    public async Task HealthCheck_running_daemon_with_recent_success_reports_healthy()
    {
        // Branch 3: daemon is running with a recent LastSuccess within 2x poll interval
        var daemon = CreateDaemonWithHealth(new DaemonHealthState(
            IsRunning: true,
            LastSuccess: DateTimeOffset.UtcNow.AddSeconds(-3),   // 3 s ago; well within 2 × 5 = 10 s window
            LastError: null,
            HighWaterSequence: 42,
            LagCount: 3,
            LastException: null
        ));

        var check = new AeroDBDaemonHealthCheck(daemon, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description!.ShouldContain("42");                // HighWaterSequence in message
        result.Description!.ShouldContain("3");                 // LagCount in message
        result.Data.ShouldContainKey("HighWaterSequence");
        result.Data.ShouldContainKey("LagCount");
        result.Data["HighWaterSequence"].ShouldBe(42L);
        result.Data["LagCount"].ShouldBe(3);
    }

    [Test]
    public async Task HealthCheck_running_daemon_without_last_success_reports_degraded()
    {
        // Branch 4a: running but LastSuccess is null → Degraded
        var daemon = CreateDaemonWithHealth(new DaemonHealthState(
            IsRunning: true,
            LastSuccess: null,
            LastError: DateTimeOffset.UtcNow.AddMinutes(-5),
            HighWaterSequence: 10,
            LagCount: 7,
            LastException: "Timeout exceeded"
        ));

        var check = new AeroDBDaemonHealthCheck(daemon, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("no recent success");
        result.Description!.ShouldContain("Timeout exceeded");
    }

    [Test]
    public async Task HealthCheck_running_daemon_with_stale_success_reports_degraded()
    {
        // Branch 4b: running but LastSuccess is outside the 2x poll-interval window → Degraded
        var daemon = CreateDaemonWithHealth(new DaemonHealthState(
            IsRunning: true,
            LastSuccess: DateTimeOffset.UtcNow.AddSeconds(-30), // 30 s ago; outside 2 × 5 = 10 s window
            LastError: null,
            HighWaterSequence: 15,
            LagCount: 2,
            LastException: null
        ));

        var check = new AeroDBDaemonHealthCheck(daemon, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("no recent success");
        result.Description!.ShouldContain("none");               // last error reported as "none"
    }

    // ─── Daemon lifecycle — start/stop ──────────────────────────────

    [Test]
    public async Task Daemon_lifecycle_start_stop()
    {
        // Create a store with events enabled (required for daemon usage)
        var store = new DocumentStore(new StoreOptions { Events = { Enabled = true } });

        // Daemon should be null initially (not started)
        store.Daemon.ShouldBeNull();

        // Create and assign daemon to the store
        var daemon = new AsyncDaemon(store, []);
        store.Daemon = daemon;

        store.Daemon.ShouldNotBeNull();
        store.Daemon.Health.IsRunning.ShouldBeFalse();

        // Start — with no async projections, no shards are created but
        // Health.IsRunning is set to true
        daemon.Start(TimeSpan.FromSeconds(5));
        store.Daemon.Health.IsRunning.ShouldBeTrue();

        // Stop — verify clean shutdown
        await daemon.StopAsync();
        store.Daemon.Health.IsRunning.ShouldBeFalse();

        // Dispose — Idempotent, should not throw
        await daemon.DisposeAsync();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Create an <see cref="AsyncDaemon"/> whose <see cref="AsyncDaemon.Health"/>
    /// property has been set to <paramref name="health"/> via the private setter.
    /// </summary>
    private static AsyncDaemon CreateDaemonWithHealth(DaemonHealthState health)
    {
        var store = new DocumentStore(new StoreOptions());
        var daemon = new AsyncDaemon(store, []);
        SetHealth(daemon, health);
        return daemon;
    }

    private static void SetHealth(AsyncDaemon daemon, DaemonHealthState health)
    {
        // AsyncDaemon.Health has a private setter — use reflection to set test state.
        var prop = typeof(AsyncDaemon).GetProperty("Health", BindingFlags.Public | BindingFlags.Instance)!;
        var setter = prop.GetSetMethod(nonPublic: true)!;
        setter.Invoke(daemon, [health]);
    }
}
