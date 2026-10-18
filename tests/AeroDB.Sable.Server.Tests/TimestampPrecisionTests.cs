using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AeroDB.Sable.Server.Tests;

/// <summary>
/// Regression coverage for POCO literals: audit/admission values must retain
/// the complete .NET tick value instead of being rounded to a whole second.
/// This fixture is deliberately separate from ServerTestHarness and the local
/// developer server.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class TimestampPrecisionTests
{
    [Test]
    [Arguments("insert")]
    [Arguments("store")]
    [Arguments("expected-version-update")]
    public async Task DateTime_utc_ticks_survive_each_poco_write_path(string writePath, CancellationToken ct)
    {
        await using var fixture = await TimestampFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var expected = new DateTime(2026, 10, 14, 1, 2, 3, DateTimeKind.Utc).AddTicks(7_654_321);
        var document = new TimestampDocument { Id = 100_000L, UtcValue = expected, OffsetValue = DateTimeOffset.UnixEpoch };

        await WriteAsync(store, document, writePath, ct);

        var persisted = await LoadAsync(fixture, document.Id, ct);
        persisted.UtcValue.Kind.ShouldBe(DateTimeKind.Utc);
        persisted.UtcValue.Ticks.ShouldBe(expected.Ticks);
    }

    [Test]
    [Arguments("insert")]
    [Arguments("store")]
    [Arguments("expected-version-update")]
    public async Task DateTimeOffset_fractional_ticks_and_nonzero_offset_normalize_to_utc_for_each_poco_write_path(string writePath, CancellationToken ct)
    {
        await using var fixture = await TimestampFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var expected = new DateTimeOffset(2026, 10, 14, 4, 5, 6, TimeSpan.FromHours(5)).AddTicks(1_234_567);
        var document = new TimestampDocument { Id = 200_000L, UtcValue = DateTime.UnixEpoch, OffsetValue = expected };

        await WriteAsync(store, document, writePath, ct);

        var persisted = await LoadAsync(fixture, document.Id, ct);
        persisted.OffsetValue.Offset.ShouldBe(TimeSpan.Zero);
        persisted.OffsetValue.UtcTicks.ShouldBe(expected.UtcTicks);
    }

    [Test]
    [Arguments("insert")]
    [Arguments("store")]
    [Arguments("expected-version-update")]
    public async Task Whole_second_timestamp_controls_remain_exact_for_each_poco_write_path(string writePath, CancellationToken ct)
    {
        await using var fixture = await TimestampFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var utc = new DateTime(2026, 10, 14, 7, 8, 9, DateTimeKind.Utc);
        var offset = new DateTimeOffset(2026, 10, 14, 10, 11, 12, TimeSpan.Zero);
        var document = new TimestampDocument { Id = 300_000L, UtcValue = utc, OffsetValue = offset };

        await WriteAsync(store, document, writePath, ct);

        var persisted = await LoadAsync(fixture, document.Id, ct);
        persisted.UtcValue.Ticks.ShouldBe(utc.Ticks);
        persisted.OffsetValue.UtcTicks.ShouldBe(offset.UtcTicks);
    }

    [Test]
    [Arguments("ar-SA")]
    [Arguments("th-TH")]
    public async Task Poco_timestamp_literals_are_invariant_under_non_gregorian_current_cultures(string cultureName, CancellationToken ct)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            await using var fixture = await TimestampFixture.StartAsync(ct);
            await using var store = await fixture.CreateStoreAsync(ct);
            var utc = new DateTime(2026, 10, 14, 13, 14, 15, DateTimeKind.Utc).AddTicks(7_000_001);
            var offset = new DateTimeOffset(2026, 10, 14, 16, 17, 18, TimeSpan.FromHours(-4)).AddTicks(123);
            var document = new TimestampDocument { Id = 400_000L, UtcValue = utc, OffsetValue = offset };

            await WriteAsync(store, document, "store", ct);

            var persisted = await LoadAsync(fixture, document.Id, ct);
            persisted.UtcValue.Ticks.ShouldBe(utc.Ticks);
            persisted.OffsetValue.UtcTicks.ShouldBe(offset.UtcTicks);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static async Task WriteAsync(IDocumentStore store, TimestampDocument document, string path, CancellationToken ct)
    {
        if (path == "expected-version-update")
        {
            var seed = new TimestampDocument
            {
                Id = document.Id,
                Version = 1L,
                UtcValue = DateTime.UnixEpoch,
                OffsetValue = DateTimeOffset.UnixEpoch
            };
            await using (var insert = await store.LightweightSessionAsync(ct))
            {
                insert.Insert([seed]);
                await insert.SaveChangesAsync(ct);
            }

            await using var update = await store.LightweightSessionAsync(ct);
            var loaded = await update.LoadAsync<TimestampDocument>(document.Id, ct);
            loaded.ShouldNotBeNull();
            loaded.UtcValue = document.UtcValue;
            loaded.OffsetValue = document.OffsetValue;
            update.UpdateExpectedVersion(loaded, 1L);
            await update.SaveChangesAsync(ct);
            loaded.Version.ShouldBe(2L);
            return;
        }

        await using (var session = await store.LightweightSessionAsync(ct))
        {
            if (path == "insert")
            {
                document.Version = 1L;
                session.Insert([document]);
            }
            else
                session.Store(document);
            await session.SaveChangesAsync(ct);
        }

        document.Version.ShouldBe(1L);
    }

    private static async Task<TimestampDocument> LoadAsync(TimestampFixture fixture, long id, CancellationToken ct)
    {
        await using var reader = await fixture.CreateStoreAsync(ct);
        await using var session = await reader.LightweightSessionAsync(ct);
        var value = await session.LoadAsync<TimestampDocument>(id, ct);
        value.ShouldNotBeNull();
        return value;
    }

    public sealed class TimestampDocument : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public DateTime UtcValue { get; set; }
        public DateTimeOffset OffsetValue { get; set; }
    }

    private sealed class TimestampFixture : IAsyncDisposable
    {
        private const string Username = "timestamp_precision_test";
        private const string Password = "timestamp-precision-local-only";
        private readonly string _containerName;
        private readonly int _port;
        private readonly string _database;
        private string? _ownedContainerId;

        private TimestampFixture(string containerName, int port, string database)
        {
            _containerName = containerName;
            _port = port;
            _database = database;
        }

        public static async Task<TimestampFixture> StartAsync(CancellationToken ct)
        {
            var fixture = new TimestampFixture($"aerodb-timestamp-precision-{Guid.NewGuid():N}", AvailablePort(), $"timestamp_{Guid.NewGuid():N}");
            fixture._ownedContainerId = await DockerAsync(["run", "-d", "--rm", "--name", fixture._containerName, "--label", "aerodb.testfixture=timestamp-precision", "-p", $"127.0.0.1:{fixture._port}:8000", "surrealdb/surrealdb:v3.2.4", "start", "--user", Username, "--pass", Password, "memory"], ct);
            try { await fixture.WaitAsync(ct); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<IDocumentStore> CreateStoreAsync(CancellationToken ct)
        {
            var store = Documents.For(options =>
            {
                options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
                options.Namespace = "timestamp_precision";
                options.Database = _database;
                options.Username = Username;
                options.Password = Password;
                options.AllDocumentsEnforceOptimisticConcurrency();
            });
            try { await store.InitializeAsync(ct); return store; }
            catch { await store.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ownedContainerId is not { } id) return;
            _ownedContainerId = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await DockerAsync(["rm", "-f", id], timeout.Token, tolerateFailure: true);
        }

        private async Task WaitAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    await using var store = await CreateStoreAsync(timeout.Token);
                    if ((await store.GetVersionAsync(timeout.Token))?.Contains("3.2.4", StringComparison.Ordinal) == true) return;
                }
                catch (Exception) when (!timeout.IsCancellationRequested) { }
                await Task.Delay(250, timeout.Token);
            }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The isolated timestamp fixture did not become ready.");
        }

        private static int AvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<string> DockerAsync(IEnumerable<string> arguments, CancellationToken ct, bool tolerateFailure = false)
        {
            var info = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Docker for the timestamp fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = (await stdout).Trim();
            if (!tolerateFailure && process.ExitCode != 0) throw new InvalidOperationException($"Timestamp fixture Docker command failed ({process.ExitCode}).");
            _ = await stderr;
            return output;
        }
    }
}
