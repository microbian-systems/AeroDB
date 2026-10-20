using System.Diagnostics;
using System.Globalization;
using System.Net.Http;

namespace AeroDB.Sable.Server.Tests;

/// <summary>
/// Regression coverage for typed reads made while a <see cref="DocumentSession"/>
/// owns an explicit transaction. The fixture is deliberately process-local and
/// never falls back to the developer server.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class ExplicitTransactionReadEnlistmentTests
{
    private const long DocumentId = 81_001;
    private static readonly DateTimeOffset CapturedOn = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments("LoadAsync")]
    [Arguments("LINQ")]
    [Arguments("CheckExistsAsync")]
    public async Task Explicit_transaction_enlists_each_typed_read_with_transaction_local_state(string readPath, CancellationToken ct)
    {
        await using var fixture = await ExplicitReadFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        await SeedAsync(store, ct);

        await using var session = await store.LightweightSessionAsync(ct);
        await using var transaction = await session.BeginTransactionAsync(ct);
        session.Insert([new ExplicitReadDocument { Id = 81_002, Version = 1, Name = "transaction-local" }]);
        await session.SaveChangesAsync(ct);
        var localRows = await session.RawQueryAsync<ExplicitReadDocument>(
            $"SELECT * FROM {store.Options.Schema.TableNameFor<ExplicitReadDocument>()}:81002;",
            null,
            ct);

        localRows.ShouldContain(row => row.Id == 81_002);
        switch (readPath)
        {
            case "LoadAsync":
                (await session.LoadAsync<ExplicitReadDocument>(81_002, ct))!.Name.ShouldBe("transaction-local");
                break;
            case "LINQ":
                (await session.Query<ExplicitReadDocument>().Where(row => row.Id == 81_002).ToListAsync(ct)).Single().Name.ShouldBe("transaction-local");
                break;
            case "CheckExistsAsync":
                (await session.CheckExistsAsync<ExplicitReadDocument>(81_002, ct)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath));
        }

        await transaction.RollbackAsync(ct);
        await using var verification = await store.QuerySessionAsync(ct);
        (await verification.CheckExistsAsync<ExplicitReadDocument>(81_002, ct)).ShouldBeFalse();
    }

    [Test]
    [Arguments("LoadAsync")]
    [Arguments("LINQ")]
    [Arguments("CheckExistsAsync")]
    public async Task Explicit_transaction_typed_read_path_keeps_the_peer_consistent_snapshot(string readPath, CancellationToken ct)
    {
        await using var fixture = await ExplicitReadFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        await SeedAsync(store, ct);

        await using var reader = await store.LightweightSessionAsync(ct);
        await using var transaction = await reader.BeginTransactionAsync(ct);
        var snapshotSql = $"SELECT * FROM {store.Options.Schema.TableNameFor<ExplicitReadDocument>()}:81001;";
        (await reader.RawQueryAsync<ExplicitReadDocument>(snapshotSql, null, ct)).Single().Version.ShouldBe(1);

        await using (var peer = await store.LightweightSessionAsync(ct))
        {
            var peerCopy = (await peer.LoadAsync<ExplicitReadDocument>(DocumentId, ct))!;
            peerCopy.Name = "peer-v2";
            peer.UpdateExpectedVersion(peerCopy, peerCopy.Version);
            await peer.SaveChangesAsync(ct);
        }

        await using (var current = await store.QuerySessionAsync(ct))
            (await current.LoadAsync<ExplicitReadDocument>(DocumentId, ct))!.Version.ShouldBe(2);

        (await reader.RawQueryAsync<ExplicitReadDocument>(snapshotSql, null, ct)).Single().Version.ShouldBe(1);
        switch (readPath)
        {
            case "LoadAsync":
                (await reader.LoadAsync<ExplicitReadDocument>(DocumentId, ct))!.Version.ShouldBe(1);
                break;
            case "LINQ":
                (await reader.Query<ExplicitReadDocument>().Where(row => row.Id == DocumentId).ToListAsync(ct)).Single().Version.ShouldBe(1);
                break;
            case "CheckExistsAsync":
                (await reader.CheckExistsAsync<ExplicitReadDocument>(DocumentId, ct)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath));
        }
        await transaction.RollbackAsync(ct);
    }

    [Test]
    [Arguments("LoadAsync")]
    [Arguments("LINQ")]
    [Arguments("CheckExistsAsync")]
    public async Task Explicit_transaction_allows_each_typed_read_mapped_to_its_own_database(string readPath, CancellationToken ct)
    {
        await using var fixture = await ExplicitReadFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct, sameDatabaseMapping: true);
        await SeedAsync(store, ct, includeSameDatabaseMapped: true);

        await using var session = await store.LightweightSessionAsync(ct);
        await using var transaction = await session.BeginTransactionAsync(ct);
        switch (readPath)
        {
            case "LoadAsync":
                (await session.LoadAsync<SameDatabaseMappedDocument>(DocumentId, ct))!.Name.ShouldBe("v1");
                break;
            case "LINQ":
                (await session.Query<SameDatabaseMappedDocument>().Where(row => row.Id == DocumentId).ToListAsync(ct)).Single().Version.ShouldBe(1);
                break;
            case "CheckExistsAsync":
                (await session.CheckExistsAsync<SameDatabaseMappedDocument>(DocumentId, ct)).ShouldBeTrue();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readPath));
        }
        await transaction.RollbackAsync(ct);
    }

    [Test]
    [Arguments("LoadAsync")]
    [Arguments("LINQ")]
    [Arguments("CheckExistsAsync")]
    public async Task Explicit_transaction_rejects_each_cross_database_typed_read_before_issuing_it(string readPath, CancellationToken ct)
    {
        await using var fixture = await ExplicitReadFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct, crossDatabaseMapping: true);

        await using var session = await store.LightweightSessionAsync(ct);
        await using var transaction = await session.BeginTransactionAsync(ct);
        InvalidOperationException exception = readPath switch
        {
            "LoadAsync" => await Should.ThrowAsync<InvalidOperationException>(() => session.LoadAsync<CrossDatabaseMappedDocument>(DocumentId, ct)),
            "LINQ" => await Should.ThrowAsync<InvalidOperationException>(() => session.Query<CrossDatabaseMappedDocument>().ToListAsync(ct)),
            "CheckExistsAsync" => await Should.ThrowAsync<InvalidOperationException>(() => session.CheckExistsAsync<CrossDatabaseMappedDocument>(DocumentId, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(readPath))
        };
        exception.Message.ShouldContain("explicit transaction");
        await transaction.RollbackAsync(ct);
    }

    [Test]
    public async Task Query_sessions_and_nontransactional_default_database_reads_remain_available(CancellationToken ct)
    {
        await using var fixture = await ExplicitReadFixture.StartAsync(ct);
        await using var store = await fixture.CreateStoreAsync(ct);
        await SeedAsync(store, ct);

        await using (var query = await store.QuerySessionAsync(ct))
        {
            (await query.LoadAsync<ExplicitReadDocument>(DocumentId, ct))!.Name.ShouldBe("v1");
            (await query.CheckExistsAsync<ExplicitReadDocument>(DocumentId, ct)).ShouldBeTrue();
        }

        await using var ordinary = await store.LightweightSessionAsync(ct);
        (await ordinary.LoadAsync<ExplicitReadDocument>(DocumentId, ct))!.Name.ShouldBe("v1");
    }

    private static async Task SeedAsync(IDocumentStore store, CancellationToken ct, bool includeSameDatabaseMapped = false)
    {
        await using var session = await store.LightweightSessionAsync(ct);
        session.Store(new ExplicitReadDocument { Id = DocumentId, Name = "v1", CreatedOn = CapturedOn });
        await session.SaveChangesAsync(ct);
        if (includeSameDatabaseMapped)
        {
            var table = store.Options.Schema.TableNameFor<SameDatabaseMappedDocument>();
            await session.RawQueryAsync<SameDatabaseMappedDocument>(
                $"CREATE {table}:{DocumentId} SET version = 1, name = 'v1';",
                null,
                ct);
        }
    }

    public sealed class ExplicitReadDocument : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public DateTimeOffset CreatedOn { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class SameDatabaseMappedDocument : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CrossDatabaseMappedDocument : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ExplicitReadFixture : IAsyncDisposable
    {
        private const string Username = "explicit_read_test";
        private const string Password = "explicit-read-local-only";
        private readonly string _container;
        private readonly int _port;
        private readonly string _namespace;
        private readonly string _database;
        private readonly string _mappedDatabase;

        private ExplicitReadFixture(string container, int port, string @namespace, string database, string mappedDatabase)
            => (_container, _port, _namespace, _database, _mappedDatabase) = (container, port, @namespace, database, mappedDatabase);

        public static async Task<ExplicitReadFixture> StartAsync(CancellationToken ct)
        {
            var token = Guid.NewGuid().ToString("N");
            var container = $"aerodb-explicit-read-{token}";
            var id = await RunDockerAsync($"run -d --rm --name {container} --label aerodb.testfixture=explicit-transaction-read -p 127.0.0.1::8000 surrealdb/surrealdb:v3.2.4 start --user {Username} --pass {Password} memory", ct);
            try
            {
                var binding = await RunDockerAsync($"port {container} 8000/tcp", ct);
                var port = int.Parse(binding[(binding.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
                var fixture = new ExplicitReadFixture(container, port, $"explicit_read_{token}", $"primary_{token}", $"mapped_{token}");
                await WaitForHealthAsync(fixture._port, ct);
                return fixture;
            }
            catch
            {
                await RunDockerAsync($"rm -f {id}", CancellationToken.None);
                throw;
            }
        }

        public async Task<IDocumentStore> CreateStoreAsync(CancellationToken ct, bool sameDatabaseMapping = false, bool crossDatabaseMapping = false, bool mappedDatabase = false)
        {
            var database = mappedDatabase ? _mappedDatabase : _database;
            var store = Documents.For(options =>
            {
                options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
                options.Namespace = _namespace;
                options.Database = database;
                options.Username = Username;
                options.Password = Password;
                options.AllDocumentsEnforceOptimisticConcurrency();
                options.Schema.AutoCreateDatabases = true;
                options.Schema.For<ExplicitReadDocument>().Identity(document => document.Id).SetSchemaMode(SchemaMode.Flexible);
                var sameDatabase = options.Schema.For<SameDatabaseMappedDocument>().Identity(document => document.Id);
                if (sameDatabaseMapping)
                    sameDatabase.Schema(_database);
                sameDatabase.SetSchemaMode(SchemaMode.Flexible);
                var crossDatabase = options.Schema.For<CrossDatabaseMappedDocument>().Identity(document => document.Id);
                if (crossDatabaseMapping && !mappedDatabase)
                    crossDatabase.Schema(_mappedDatabase);
                crossDatabase.SetSchemaMode(SchemaMode.Flexible);
            });
            await store.InitializeAsync(ct);
            return store;
        }

        public async ValueTask DisposeAsync()
        {
            await RunDockerAsync($"rm -f {_container}", CancellationToken.None);
        }

        private static async Task<string> RunDockerAsync(string arguments, CancellationToken ct)
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException("Could not launch Docker for the isolated explicit-read fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException("Isolated explicit-read fixture Docker command failed.");
            return output.Trim();
        }

        private static async Task WaitForHealthAsync(int port, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(250, ct);
            }
            throw new InvalidOperationException("Isolated explicit-read fixture did not become healthy.");
        }

    }
}
