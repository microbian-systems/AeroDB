using System.Diagnostics;
using AeroDB.WolverineFx;
using JasperFx.MultiTenancy;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using SurrealDb.Net.Models.Response;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class AeroDBCompositeSoloTests
{
    [Test]
    public async Task Late_discovered_tenant_stores_are_ready_for_global_lifecycle_operations(
        CancellationToken ct)
    {
        await using var fixture = await AuthenticatedFixture.StartAsync(ct);
        var alpha = Location("alpha");
        var beta = Location("beta");
        var directory = new Directory([]);
        var documents = new TenantDocuments(fixture);
        await using var host = await RunningHost.StartAsync(fixture, directory, documents, ct);

        directory.Set([alpha, beta]);
        var composite = host.Store.ShouldBeOfType<MultiTenantedMessageStore>();

        await composite.GetDatabaseAsync(alpha.TenantId);

        host.Source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha", "beta"]);
        await ((IMessageInbox)composite).ReleaseIncomingAsync(
            ownerId: 1,
            receivedAt: new Uri("loopback://late-tenant-readiness"));
    }

    [Test]
    public async Task Authenticated_http_solo_host_discovers_tenants_without_main_fallback_and_stops_cleanly(
        CancellationToken ct)
    {
        await using var fixture = await AuthenticatedFixture.StartAsync(ct);
        await using var main = await fixture.CreateInitializedStoreAsync("coordinator", "main", ct);
        var directory = new Directory([Location("alpha")]);
        var documents = new TenantDocuments(fixture);
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDocumentStore>(main);
                services.AddSingleton<ISurrealDbClient>(main.Client);
            })
            .UseWolverine(options =>
            {
                options.RestoreV5Defaults();
                options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                options.Durability.Mode = DurabilityMode.Solo;
                options.PersistMessagesWithAeroDBTenancy(directory, documents);
            })
            .Build();
        var disposed = false;

        try
        {
            await host.StartAsync(ct);

            var store = host.Services.GetRequiredService<IMessageStore>();
            var source = host.Services.GetRequiredService<AeroDBTenantedMessageSource>();
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            store.ShouldBeOfType<MultiTenantedMessageStore>();
            store.Role.ShouldBe(MessageStoreRole.Composite);
            runtime.Options.Durability.AssignedNodeNumber.ShouldBe(1);
            host.Services.GetService<AeroDBMessageStore>().ShouldBeNull();
            source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha"]);

            directory.Set([Location("alpha"), Location("beta")]);
            await source.RefreshAsync();

            var alpha = await source.FindAsync("alpha");
            var beta = await source.FindAsync("beta");
            alpha.Uri.ShouldNotBe(beta.Uri);
            source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha", "beta"]);
            await Should.ThrowAsync<UnknownTenantIdException>(() => source.FindAsync("unknown").AsTask());

            await host.StopAsync(ct);
            await ((IAsyncDisposable)host).DisposeAsync();
            disposed = true;

            // The composite facade borrows the main document-store client.
            (await main.GetVersionAsync(ct)).ShouldNotBeNull();
        }
        finally
        {
            if (!disposed)
            {
                await ((IAsyncDisposable)host).DisposeAsync();
            }
        }
    }

    [Test]
    public async Task Authenticated_composite_main_facade_round_trips_node_authority(CancellationToken ct)
    {
        await using var fixture = await AuthenticatedFixture.StartAsync(ct);
        var directory = new Directory([Location("alpha")]);
        var documents = new TenantDocuments(fixture);
        await using var host = await RunningHost.StartAsync(fixture, directory, documents, ct);
        var node = new Wolverine.Runtime.Agents.WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 73,
            Description = "composite-authority-round-trip",
            ControlUri = new Uri("loopback://composite-authority-round-trip"),
            Version = new Version(6, 17, 1),
            Started = DateTimeOffset.UtcNow,
            LastHealthCheck = DateTimeOffset.UtcNow
        };

        host.Store.Role.ShouldBe(MessageStoreRole.Composite);
        await host.Store.Nodes.PersistAsync(node, ct);

        var persisted = (await host.Store.Nodes.LoadAllNodesAsync(ct))
            .Single(candidate => candidate.NodeId == node.NodeId);

        persisted.AssignedNodeNumber.ShouldBe(node.AssignedNodeNumber);
        persisted.Description.ShouldBe(node.Description);
        persisted.ControlUri.ShouldBe(node.ControlUri);
        persisted.Version.ShouldBe(node.Version);
        persisted.Started.ShouldBe(node.Started);
        persisted.LastHealthCheck.ShouldBe(node.LastHealthCheck);

        await host.Store.Nodes.DeleteAsync(node.NodeId, node.AssignedNodeNumber);
    }

    [Test]
    public async Task Authenticated_composite_solo_restart_releases_tenant_ownership_without_cross_tenant_fallback(
        CancellationToken ct)
    {
        await using var fixture = await AuthenticatedFixture.StartAsync(ct);
        var locations = new[] { Location("alpha"), Location("beta") };
        var alpha = await SeedTenantOwnershipAsync(fixture, locations[0], ct);
        var beta = await SeedTenantOwnershipAsync(fixture, locations[1], ct);
        var directory = new Directory(locations);
        var documents = new TenantDocuments(fixture);

        await using (var first = await RunningHost.StartAsync(fixture, directory, documents, ct))
        {
            first.Runtime.Options.Durability.AssignedNodeNumber.ShouldBe(1);
            first.Source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha", "beta"]);
            await WaitForAllOwnershipReleasedAsync(fixture, locations[0], alpha, ct);
            await WaitForAllOwnershipReleasedAsync(fixture, locations[1], beta, ct);
        }

        await using (var restarted = await RunningHost.StartAsync(fixture, directory, documents, ct))
        {
            restarted.Runtime.Options.Durability.AssignedNodeNumber.ShouldBe(1);
            restarted.Source.AllActiveByTenant().Select(x => x.TenantId).ShouldBe(["alpha", "beta"]);
            await WaitForAllOwnershipReleasedAsync(fixture, locations[0], alpha, ct);
            await WaitForAllOwnershipReleasedAsync(fixture, locations[1], beta, ct);
        }
    }

    private static async Task<SeededOwnership> SeedTenantOwnershipAsync(
        AuthenticatedFixture fixture,
        AeroDBTenantStoreLocation location,
        CancellationToken ct)
    {
        await using var document = await fixture.CreateInitializedStoreAsync(location.Namespace, location.Database, ct);
        var store = new AeroDBMessageStore(document.Client, NullLogger<AeroDBMessageStore>.Instance,
            loggerFactory: null, storeOptions: document.Options, ownsClient: false, storeIdentity: location.PhysicalIdentity);
        await store.InitializeSchemaAsync();
        var stale = Incoming(location.TenantId + "-stale");
        var active = Incoming(location.TenantId + "-active");
        await store.StoreIncomingAsync([stale, active]);
        await store.ReassignIncomingAsync(41, [stale]);
        await store.ReassignIncomingAsync(77, [active]);
        await store.DisposeAsync();
        return new SeededOwnership(stale.Id, active.Id);
    }

    private static async Task WaitForAllOwnershipReleasedAsync(
        AuthenticatedFixture fixture,
        AeroDBTenantStoreLocation location,
        SeededOwnership expected,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var document = await fixture.CreateInitializedStoreAsync(
            location.Namespace,
            location.Database,
            timeout.Token);
        int? staleOwner = null;
        int? activeOwner = null;
        while (!timeout.IsCancellationRequested)
        {
            staleOwner = await OwnerIdAsync(document, expected.StaleId, timeout.Token);
            activeOwner = await OwnerIdAsync(document, expected.ActiveId, timeout.Token);
            if (staleOwner == 0 && activeOwner == 0)
                return;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Tenant '{location.TenantId}' did not release Solo-mode ownership. " +
            $"Observed stale={staleOwner}, active={activeOwner}.");
    }

    private static async Task<int> OwnerIdAsync(IDocumentStore document, Guid id, CancellationToken ct)
    {
        var response = await document.Client.RawQuery(
            $"SELECT owner_id FROM wolverine_incoming_envelopes:`{id}`;", cancellationToken: ct);
        response.EnsureAllOks();
        var rows = response.GetValue<List<OwnerRow>>(0) ?? [];
        rows.Count.ShouldBe(1);
        return rows[0].owner_id;
    }

    private static Envelope Incoming(string destination) => new()
    {
        Id = Guid.NewGuid(),
        Data = "composite-recovery"u8.ToArray(),
        MessageType = "tests.composite-recovery",
        Destination = new Uri("loopback://" + destination),
        Status = EnvelopeStatus.Incoming
    };

    private sealed record SeededOwnership(Guid StaleId, Guid ActiveId);

    private sealed class OwnerRow
    {
        public int owner_id { get; set; }
    }

    private static AeroDBTenantStoreLocation Location(string tenant) =>
        new(tenant, "isolated", "ca_" + tenant, "main");

    private sealed class Directory(IReadOnlyList<AeroDBTenantStoreLocation> initial) : IAeroDBTenantStoreDirectory
    {
        private IReadOnlyList<AeroDBTenantStoreLocation> _locations = initial;

        public void Set(IReadOnlyList<AeroDBTenantStoreLocation> locations) => _locations = locations;

        public ValueTask<IReadOnlyList<AeroDBTenantStoreLocation>> ListActiveAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_locations);
    }

    private sealed class TenantDocuments(AuthenticatedFixture fixture) : IAeroDBTenantDocumentStoreFactory
    {
        public ValueTask<IDocumentStore> CreateAsync(
            AeroDBTenantStoreLocation location,
            CancellationToken cancellationToken = default) =>
            fixture.CreateUninitializedStoreAsync(location.Namespace, location.Database, cancellationToken);
    }

    private sealed class RunningHost(IHost host, IDocumentStore main) : IAsyncDisposable
    {
        private IHost Host { get; } = host;

        public AeroDBTenantedMessageSource Source =>
            Host.Services.GetRequiredService<AeroDBTenantedMessageSource>();

        public IMessageStore Store => Host.Services.GetRequiredService<IMessageStore>();

        public IWolverineRuntime Runtime => Host.Services.GetRequiredService<IWolverineRuntime>();

        public static async Task<RunningHost> StartAsync(
            AuthenticatedFixture fixture,
            IAeroDBTenantStoreDirectory directory,
            IAeroDBTenantDocumentStoreFactory documents,
            CancellationToken ct)
        {
            var main = await fixture.CreateInitializedStoreAsync("coordinator", "main", ct);
            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(main);
                    services.AddSingleton<ISurrealDbClient>(main.Client);
                })
                .UseWolverine(options =>
                {
                    options.RestoreV5Defaults();
                    options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                    options.Durability.Mode = DurabilityMode.Solo;
                    options.PersistMessagesWithAeroDBTenancy(directory, documents);
                })
                .Build();

            try
            {
                await host.StartAsync(ct);
                return new RunningHost(host, main);
            }
            catch
            {
                await ((IAsyncDisposable)host).DisposeAsync();
                await main.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)Host).DisposeAsync();
            await main.DisposeAsync();
        }
    }

    private sealed class AuthenticatedFixture : IAsyncDisposable
    {
        private const string Username = "aerodb_composite_test";
        private const string Password = "aerodb-composite-local-only";
        private readonly string _containerName;
        private readonly int _port;

        private AuthenticatedFixture(string containerName, int port)
        {
            _containerName = containerName;
            _port = port;
        }

        public static async Task<AuthenticatedFixture> StartAsync(CancellationToken ct)
        {
            var fixture = new AuthenticatedFixture(
                "aerodb-composite-" + Guid.NewGuid().ToString("N"),
                GetAvailableLoopbackPort());
            var arguments =
                $"run -d --rm --name {fixture._containerName} --label aerodb.testfixture=composite-solo " +
                $"-p 127.0.0.1:{fixture._port}:8000 surrealdb/surrealdb:v3.2.4 " +
                $"start --user {Username} --pass {Password} memory";

            await RunDockerAsync(arguments, ct);
            try
            {
                await fixture.WaitUntilReadyAsync(ct);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask<IDocumentStore> CreateInitializedStoreAsync(
            string @namespace,
            string database,
            CancellationToken ct)
        {
            await EnsureScopeAsync(@namespace, database, ct);
            var store = CreateStore(@namespace, database);
            try
            {
                await store.InitializeAsync(ct);
                return store;
            }
            catch
            {
                await store.DisposeAsync();
                throw;
            }
        }

        public async ValueTask<IDocumentStore> CreateUninitializedStoreAsync(
            string @namespace,
            string database,
            CancellationToken ct)
        {
            await EnsureScopeAsync(@namespace, database, ct);
            return CreateStore(@namespace, database);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunDockerAsync($"rm -f {_containerName}", timeout.Token, tolerateFailure: true);
        }

        private async Task EnsureScopeAsync(string @namespace, string database, CancellationToken ct)
        {
            await using var bootstrap = CreateStore("bootstrap", "bootstrap");
            await bootstrap.InitializeAsync(ct);
            await using var session = await bootstrap.LightweightSessionAsync(ct);
            await session.ExecuteSqlAsync($"DEFINE NAMESPACE IF NOT EXISTS `{@namespace}`;", null, ct);
            await session.ExecuteSqlAsync($"DEFINE DATABASE IF NOT EXISTS `{database}`;", null, ct);
        }

        private async Task WaitUntilReadyAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Exception? lastFailure = null;

            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    await using var store = CreateStore("bootstrap", "bootstrap");
                    await store.InitializeAsync(timeout.Token);
                    var version = await store.GetVersionAsync(timeout.Token);
                    if (version?.Contains("3.2.4", StringComparison.Ordinal) == true)
                        return;
                    lastFailure = new InvalidOperationException("Unexpected SurrealDB version.");
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"The isolated authenticated SurrealDB fixture did not become ready. Last failure: {lastFailure?.GetType().Name ?? "none"}.");
        }

        private IDocumentStore CreateStore(string @namespace, string database) => Documents.For(options =>
        {
            options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
            options.Namespace = @namespace;
            options.Database = database;
            options.Username = Username;
            options.Password = Password;
        });

        private static int GetAvailableLoopbackPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task RunDockerAsync(string arguments, CancellationToken ct, bool tolerateFailure = false)
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start Docker for the isolated SurrealDB fixture.");

            var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
            var standardError = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (!tolerateFailure && process.ExitCode != 0)
                throw new InvalidOperationException($"Docker command failed ({process.ExitCode}): {await standardError}{await standardOutput}");
        }
    }
}
