using System.Diagnostics;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class GeneratedMaterializationTests
{
    [Test]
    public async Task Generated_document_load_and_query_materialize_in_an_authenticated_isolated_server(CancellationToken ct)
    {
        var events = new List<string>();
        var listenerSubscriptions = new List<IDisposable>();
        using var listenerSubscription = DiagnosticListener.AllListeners.Subscribe(
            new MaterializationListenerObserver(listener =>
            {
                if (listener.Name == "AeroDB.Sable.Materialization")
                    listenerSubscriptions.Add(listener.Subscribe(new MaterializationEventObserver((name, _) => events.Add(name))));
            }));
        using var listenerCleanup = new CompositeDisposable(listenerSubscriptions);
        await using var fixture = await AuthenticatedSurrealFixture.StartAsync(ct);
        (await fixture.IsAnonymousSqlDeniedAsync(ct)).ShouldBeTrue();
        await using var store = await fixture.CreateStoreAsync(ct);
        var document = new GeneratedServerDocument
        {
            Id = 9_223_372_036_854_775_000L,
            Name = "generated materialization",
            CreatedOn = new DateTimeOffset(2026, 10, 11, 15, 0, 0, TimeSpan.Zero),
            ModifiedOn = new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero),
            CreatedBy = "creator@example.test",
            ModifiedBy = "editor@example.test",
            Budget = 4000.25m,
            Lines = [new GeneratedServerLine { Code = "SAM", Quantity = 2 }]
        };

        await using (var writer = await store.LightweightSessionAsync(ct))
        {
            writer.Store(document);
            await writer.SaveChangesAsync(ct);
        }

        await using var reader = await fixture.CreateStoreAsync(ct);
        await using var loaded = await reader.LightweightSessionAsync(ct);
        var byIdentity = await loaded.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        var queried = await loaded.Query<GeneratedServerDocument>()
            .Where(candidate => candidate.Id == document.Id)
            .ToListAsync(ct);
        var taggedDecimal = await loaded.RawQueryAsync<GeneratedServerDocument>(
            "SELECT id, 4000.25dec AS budget FROM generated_server_document WHERE id = type::record($id);",
            new Dictionary<string, object?> { ["id"] = $"generated_server_document:{document.Id}" },
            ct);

        byIdentity.ShouldNotBeNull();
        byIdentity.Id.ShouldBe(document.Id);
        byIdentity.Version.ShouldBe(document.Version);
        byIdentity.CreatedOn.ShouldBe(document.CreatedOn);
        byIdentity.ModifiedOn.ShouldBe(document.ModifiedOn);
        byIdentity.CreatedBy.ShouldBe(document.CreatedBy);
        byIdentity.ModifiedBy.ShouldBe(document.ModifiedBy);
        byIdentity.Budget.ShouldBe(4000.25m);
        byIdentity.Name.ShouldBe("generated materialization");
        byIdentity.Lines.Single().Code.ShouldBe("SAM");
        queried.Single().Id.ShouldBe(document.Id);
        queried.Single().Version.ShouldBe(document.Version);
        queried.Single().Lines.Single().Quantity.ShouldBe(2);
        taggedDecimal.Single().Budget.ShouldBe(4000.25m);
        events.ShouldContain("generated");
        events.ShouldNotContain("generated-dispatch-miss");
        events.ShouldNotContain("legacy");
    }

    [Test]
    public async Task Generated_document_handles_partial_raw_rows_empty_queries_and_optimistic_stale_updates(CancellationToken ct)
    {
        var metadata = MetadataRegistry.TryGet<GeneratedServerDocument>();
        metadata.ShouldNotBeNull();
        metadata.HasVersion.ShouldBeTrue();
        metadata.VersionFieldName.ShouldBe("Version");
        await using var fixture = await AuthenticatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var document = new GeneratedServerDocument
        {
            Id = 9_223_372_036_854_775_001L,
            Name = "first",
            CreatedOn = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)
        };

        await using (var writer = await store.LightweightSessionAsync(ct))
        {
            writer.Store(document);
            await writer.SaveChangesAsync(ct);
        }

        await using var raw = await fixture.CreateStoreAsync(ct);
        await using var partial = await raw.LightweightSessionAsync(ct);
        var partialRows = await partial.RawQueryAsync<GeneratedServerDocument>(
            "SELECT id, name FROM generated_server_document WHERE id = type::record($id);",
            new Dictionary<string, object?> { ["id"] = $"generated_server_document:{document.Id}" },
            ct);
        var none = await partial.Query<GeneratedServerDocument>()
            .Where(candidate => candidate.Name == "does-not-exist")
            .ToListAsync(ct);

        partialRows.Single().Id.ShouldBe(document.Id);
        partialRows.Single().Name.ShouldBe("first");
        partialRows.Single().Lines.ShouldBeEmpty();
        none.ShouldBeEmpty();

        await using var firstStore = await fixture.CreateStoreAsync(ct);
        await using var secondStore = await fixture.CreateStoreAsync(ct);
        await using var first = await firstStore.LightweightSessionAsync(ct);
        await using var second = await secondStore.LightweightSessionAsync(ct);
        var firstCopy = (await first.LoadAsync<GeneratedServerDocument>(document.Id, ct))!;
        var staleCopy = (await second.LoadAsync<GeneratedServerDocument>(document.Id, ct))!;
        var expectedVersion = staleCopy.Version;
        firstCopy.Version.ShouldBe(document.Version);
        staleCopy.Version.ShouldBe(document.Version);
        firstCopy.Name = "updated";
        first.Store(firstCopy);
        await first.SaveChangesAsync(ct);
        firstCopy.Version.ShouldBe(expectedVersion + 1);
        staleCopy.Name = "stale";
        second.Store(staleCopy);

        var exception = await Should.ThrowAsync<ConcurrencyException>(() => second.SaveChangesAsync(ct));
        exception.ExpectedVersion.ShouldBe(expectedVersion);
        exception.ActualVersion.ShouldBe(expectedVersion + 1);

        await using var verification = await fixture.CreateStoreAsync(ct);
        await using var verificationSession = await verification.LightweightSessionAsync(ct);
        var winner = await verificationSession.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        winner!.Name.ShouldBe("updated");
        winner.Version.ShouldBe(expectedVersion + 1);
    }

    [Test]
    public async Task Explicit_update_of_a_tracked_generated_document_increments_and_persists_the_version(CancellationToken ct)
    {
        await using var fixture = await AuthenticatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var document = new GeneratedServerDocument
        {
            Id = 9_223_372_036_854_775_002L,
            Name = "before explicit update"
        };

        await using (var writer = await store.LightweightSessionAsync(ct))
        {
            writer.Store(document);
            await writer.SaveChangesAsync(ct);
        }

        document.Version.ShouldBe(1);

        await using (var updater = await store.LightweightSessionAsync(ct))
        {
            var tracked = await updater.LoadAsync<GeneratedServerDocument>(document.Id, ct);
            tracked.ShouldNotBeNull();
            tracked.Name = "after explicit update";
            updater.Update(tracked);
            await updater.SaveChangesAsync(ct);
            tracked.Version.ShouldBe(2);
        }

        await using var verification = await fixture.CreateStoreAsync(ct);
        await using var verificationSession = await verification.LightweightSessionAsync(ct);
        var persisted = await verificationSession.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("after explicit update");
        persisted.Version.ShouldBe(2);
    }

    [Test]
    public async Task Explicit_update_of_a_stale_generated_document_throws_and_preserves_the_winner(CancellationToken ct)
    {
        await using var fixture = await AuthenticatedSurrealFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        var document = new GeneratedServerDocument
        {
            Id = 9_223_372_036_854_775_003L,
            Name = "original"
        };

        await using (var writer = await store.LightweightSessionAsync(ct))
        {
            writer.Store(document);
            await writer.SaveChangesAsync(ct);
        }

        document.Version.ShouldBe(1);

        await using var firstStore = await fixture.CreateStoreAsync(ct);
        await using var secondStore = await fixture.CreateStoreAsync(ct);
        await using var first = await firstStore.LightweightSessionAsync(ct);
        await using var second = await secondStore.LightweightSessionAsync(ct);
        var winner = await first.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        var stale = await second.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        winner.ShouldNotBeNull();
        stale.ShouldNotBeNull();
        winner.Version.ShouldBe(1);
        stale.Version.ShouldBe(1);

        winner.Name = "winner";
        first.Update(winner);
        await first.SaveChangesAsync(ct);

        stale.Name = "stale";
        second.Update(stale);
        var exception = await Should.ThrowAsync<ConcurrencyException>(() => second.SaveChangesAsync(ct));
        exception.ExpectedVersion.ShouldBe(1);
        exception.ActualVersion.ShouldBe(2);

        await using var verification = await fixture.CreateStoreAsync(ct);
        await using var verificationSession = await verification.LightweightSessionAsync(ct);
        var persisted = await verificationSession.LoadAsync<GeneratedServerDocument>(document.Id, ct);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("winner");
        persisted.Version.ShouldBe(2);
    }

    public abstract class GeneratedServerAuditedDocument : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public DateTimeOffset CreatedOn { get; set; }
        public DateTimeOffset ModifiedOn { get; set; }
        public string? CreatedBy { get; set; }
        public string? ModifiedBy { get; set; }
    }

    public sealed class GeneratedServerDocument : GeneratedServerAuditedDocument
    {
        public string Name { get; set; } = string.Empty;
        public decimal Budget { get; set; }
        public List<GeneratedServerLine> Lines { get; set; } = [];
    }

    public sealed class GeneratedServerLine
    {
        public string Code { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class AuthenticatedSurrealFixture : IAsyncDisposable
    {
        private readonly string _containerName;
        private readonly int _port;
        private readonly string _database;
        private const string Username = "generated_materialization_test";
        private const string Password = "generated-materialization-local-only";

        private AuthenticatedSurrealFixture(string containerName, int port, string database)
        {
            _containerName = containerName;
            _port = port;
            _database = database;
        }

        public static async Task<AuthenticatedSurrealFixture> StartAsync(CancellationToken ct)
        {
            var fixture = new AuthenticatedSurrealFixture(
                $"aerodb-generated-materialization-{Guid.NewGuid():N}",
                GetAvailableLoopbackPort(),
                $"generated_{Guid.NewGuid():N}");
            var arguments =
                $"run -d --rm --name {fixture._containerName} --label aerodb.testfixture=generated-materialization " +
                $"-p 127.0.0.1:{fixture._port}:8000 surrealdb/surrealdb:v3.2.4 " +
                // SurrealDB v3 authenticates by default; `--unauthenticated` is
                // the explicit opt-out and is intentionally absent.
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

        public async Task<IDocumentStore> CreateStoreAsync(CancellationToken ct)
        {
            var store = Documents.For(options =>
            {
                options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
                options.Namespace = "generated_materialization";
                options.Database = _database;
                options.Username = Username;
                options.Password = Password;
                options.AllDocumentsEnforceOptimisticConcurrency();
            });

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

        public async Task<bool> IsAnonymousSqlDeniedAsync(CancellationToken ct)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/sql")
            {
                Content = new StringContent("RETURN 1;")
            };
            using var response = await client.SendAsync(request, ct);
            return response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunDockerAsync($"rm -f {_containerName}", timeout.Token, tolerateFailure: true);
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
                    await using var store = await CreateStoreAsync(timeout.Token);
                    if ((await store.GetVersionAsync(timeout.Token))?.Contains("3.2.4", StringComparison.Ordinal) == true)
                        return;
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

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (!tolerateFailure && process.ExitCode != 0)
                throw new InvalidOperationException($"Docker command failed ({process.ExitCode}): {await error}{await output}");
        }
    }

    private sealed class MaterializationListenerObserver(Action<DiagnosticListener> onListener) : IObserver<DiagnosticListener>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(DiagnosticListener value) => onListener(value);
    }

    private sealed class MaterializationEventObserver(Action<string, object?> onEvent) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(KeyValuePair<string, object?> value) => onEvent(value.Key, value.Value);
    }

    private sealed class CompositeDisposable(List<IDisposable> subscriptions) : IDisposable
    {
        public void Dispose()
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
        }
    }
}
