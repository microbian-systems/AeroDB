using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dali.Tests;

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

    // ─── DaliDaemonHealthCheck tests ──────────────────────────────────

    [Test]
    public async Task HealthCheck_null_daemon_reports_healthy()
    {
        var check = new DaliDaemonHealthCheck(null, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Test]
    public async Task HealthCheck_stopped_daemon_reports_unhealthy()
    {
        var store = new DocumentStore(new StoreOptions());
        var daemon = new AsyncDaemon(store, []);
        var check = new DaliDaemonHealthCheck(daemon, TimeSpan.FromSeconds(5));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };

        var result = await check.CheckHealthAsync(context);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldContain("stopped");
    }
}
