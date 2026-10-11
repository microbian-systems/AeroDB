using System.Diagnostics;
using SurrealDb.Net.Exceptions;

namespace AeroDB.Sable.Server.Tests;

/// <summary>
/// Exercises the real server transaction boundary for explicit expected-version writes.
/// This is deliberately a server test: the embedded test coordinator serializes writers
/// and cannot demonstrate two independent database transactions racing after their reads.
/// </summary>
[Category("ServerIntegration")]
[NotInParallel]
public sealed class VaultConcurrencyRegressionTests
{
    [Test]
    public async Task Two_independent_stores_racing_after_expected_version_reads_commit_exactly_one_complete_update(
        CancellationToken ct)
    {
        await using var fixture = await ExpectedVersionRaceFixture.StartAsync(ct);
        var barrier = new PostVersionReadBarrier();

        await using var seedStore = await fixture.CreateStoreAsync(ct);
        await SeedAsync(seedStore, ct);

        await using var firstStore = await fixture.CreateStoreAsync(barrier, ct);
        await using var secondStore = await fixture.CreateStoreAsync(barrier, ct);

        var firstAttempt = SaveExpectedVersionUpdateAsync(
            firstStore,
            writer: "writer-one",
            siblingId: 8_900_011L,
            ct);
        var secondAttempt = SaveExpectedVersionUpdateAsync(
            secondStore,
            writer: "writer-two",
            siblingId: 8_900_012L,
            ct);

        RaceAttempt[] outcomes;
        try
        {
            await barrier.WaitUntilBothWritersReachedThePostReadBarrierAsync(ct);
            barrier.ReleaseWriters();
            outcomes = await Task.WhenAll(firstAttempt, secondAttempt);
        }
        finally
        {
            // Never leave a test worker held if the assertion or test token fails.
            barrier.ReleaseWriters();
        }

        outcomes.Count(outcome => outcome.Failure is null).ShouldBe(1);
        var winningOutcome = outcomes.Single(outcome => outcome.Failure is null);
        var failedOutcome = outcomes.Single(outcome => outcome.Failure is not null);
        failedOutcome.Failure.ShouldNotBeNull();
        IsConcurrencyRejection(failedOutcome.Failure).ShouldBeTrue();

        await using var verificationStore = await fixture.CreateStoreAsync(ct);
        await using var verification = await verificationStore.LightweightSessionAsync(ct);
        var persisted = await verification.LoadAsync<VersionedVaultContract>(8_900_001L, ct);
        var siblings = await verification.Query<VaultRaceSibling>().ToListAsync(ct);

        persisted.ShouldNotBeNull();
        persisted.Version.ShouldBe(2);
        persisted.Writer.ShouldBe(winningOutcome.Writer);
        persisted.Payload.ShouldBe(winningOutcome.Payload);

        siblings.Count.ShouldBe(1);
        siblings.Single().Id.ShouldBe(winningOutcome.SiblingId);
        siblings.Single().Writer.ShouldBe(winningOutcome.Writer);
        siblings.Select(sibling => sibling.Id).ShouldNotContain(failedOutcome.SiblingId);
    }

    [Test]
    public async Task A_late_sibling_failure_rolls_back_the_expected_version_update_and_all_siblings(CancellationToken ct)
    {
        await using var fixture = await ExpectedVersionRaceFixture.StartAsync(ct);
        await using var seedStore = await fixture.CreateStoreAsync(ct);
        await SeedAsync(seedStore, ct);

        await using (var failingStore = await fixture.CreateStoreAsync(new LateSiblingFailureListener(), ct))
        await using (var session = await failingStore.LightweightSessionAsync(ct))
        {
            await using var transaction = await session.BeginTransactionAsync(ct);
            var replacement = new VersionedVaultContract
            {
                Id = 8_900_001L,
                Version = 2,
                Writer = "late-failure-writer",
                Payload = "complete payload for late-failure-writer"
            };

            session.UpdateExpectedVersion(replacement, expectedVersion: 1);
            session.Store(new VaultRaceSibling { Id = 8_900_021L, Writer = replacement.Writer });

            var exception = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(ct));
            exception.Message.ShouldBe("Failed to save changes.");
            exception.InnerException.ShouldBeOfType<InvalidOperationException>();
            exception.InnerException!.Message.ShouldBe(LateSiblingFailureListener.FailureMessage);
        }

        await using var verificationStore = await fixture.CreateStoreAsync(ct);
        await using var verification = await verificationStore.LightweightSessionAsync(ct);
        var persisted = await verification.LoadAsync<VersionedVaultContract>(8_900_001L, ct);
        var siblings = await verification.Query<VaultRaceSibling>().ToListAsync(ct);

        persisted.ShouldNotBeNull();
        persisted.Version.ShouldBe(1);
        persisted.Writer.ShouldBe("seed");
        persisted.Payload.ShouldBe("original complete payload");
        siblings.ShouldBeEmpty();
    }

    private static async Task SeedAsync(IDocumentStore store, CancellationToken ct)
    {
        await using var session = await store.LightweightSessionAsync(ct);
        session.Store(new VersionedVaultContract
        {
            Id = 8_900_001L,
            Version = 1,
            Writer = "seed",
            Payload = "original complete payload"
        });
        await session.SaveChangesAsync(ct);
    }

    private static async Task<RaceAttempt> SaveExpectedVersionUpdateAsync(
        IDocumentStore store,
        string writer,
        long siblingId,
        CancellationToken ct)
    {
        try
        {
            await using var session = await store.LightweightSessionAsync(ct);
            await using var transaction = await session.BeginTransactionAsync(ct);

            session.UpdateExpectedVersion(
                new VersionedVaultContract
                {
                    Id = 8_900_001L,
                    Version = 2,
                    Writer = writer,
                    Payload = $"complete payload for {writer}"
                },
                expectedVersion: 1);
            session.Store(new VaultRaceSibling { Id = siblingId, Writer = writer });

            await session.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new RaceAttempt(writer, siblingId, $"complete payload for {writer}", null);
        }
        catch (Exception exception)
        {
            return new RaceAttempt(writer, siblingId, $"complete payload for {writer}", exception);
        }
    }

    private static bool IsConcurrencyRejection(Exception exception)
    {
        foreach (var candidate in Unwrap(exception))
        {
            if (candidate is ConcurrencyException)
            {
                return true;
            }

            if (candidate is SurrealDbException
                && candidate.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private sealed record RaceAttempt(string Writer, long SiblingId, string Payload, Exception? Failure);

    /// <summary>
    /// Public so the existing source-generator test project produces Sable metadata;
    /// this regression must not depend on the reflection materialization fallback.
    /// </summary>
    public sealed class VersionedVaultContract : SableDocument<long>, IVersioned
    {
        public long Version { get; set; }
        public string Writer { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    /// <summary>Public generated document used to prove that a losing transaction has no side effects.</summary>
    public sealed class VaultRaceSibling : SableDocument<long>
    {
        public string Writer { get; set; } = string.Empty;
    }

    private sealed class PostVersionReadBarrier : DocumentSessionListenerBase
    {
        private readonly TaskCompletionSource<bool> _bothWritersArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseWriters = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writerArrivals;

        public override async Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
        {
            if (entity is not VersionedVaultContract)
            {
                return;
            }

            if (Interlocked.Increment(ref _writerArrivals) == 2)
            {
                _bothWritersArrived.TrySetResult(true);
            }

            await _releaseWriters.Task.WaitAsync(ct);
        }

        public async Task WaitUntilBothWritersReachedThePostReadBarrierAsync(CancellationToken ct)
        {
            await _bothWritersArrived.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }

        public void ReleaseWriters() => _releaseWriters.TrySetResult(true);
    }

    private sealed class LateSiblingFailureListener : DocumentSessionListenerBase
    {
        public const string FailureMessage = "synthetic late sibling persistence failure";

        public override Task AfterSaveChangesAsync(IDocumentSession session, CancellationToken ct)
            => Task.FromException(new InvalidOperationException(FailureMessage));
    }

    private sealed class ExpectedVersionRaceFixture : IAsyncDisposable
    {
        private const string Username = "expected_version_test";
        private const string Password = "expected-version-local-only";
        private readonly string _containerName;
        private readonly int _port;
        private readonly string _namespace;
        private readonly string _database;

        private ExpectedVersionRaceFixture(string containerName, int port, string testSuffix)
        {
            _containerName = containerName;
            _port = port;
            _namespace = $"expected_version_{testSuffix}";
            _database = $"race_{testSuffix}";
        }

        public static async Task<ExpectedVersionRaceFixture> StartAsync(CancellationToken ct)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var fixture = new ExpectedVersionRaceFixture(
                $"aerodb-expected-version-{suffix}",
                GetAvailableLoopbackPort(),
                suffix);
            var arguments =
                $"run -d --rm --name {fixture._containerName} --label aerodb.testfixture=expected-version-race " +
                $"-p 127.0.0.1:{fixture._port}:8000 surrealdb/surrealdb:v3.2.4 " +
                $"start --user {Username} --pass {Password} memory";

            try
            {
                await RunDockerAsync(arguments, ct);
                await fixture.WaitUntilReadyAsync(ct);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Task<IDocumentStore> CreateStoreAsync(CancellationToken ct) => CreateStoreAsync(null, ct);

        public async Task<IDocumentStore> CreateStoreAsync(IDocumentSessionListener? listener, CancellationToken ct)
        {
            var store = Documents.For(options =>
            {
                options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
                options.Namespace = _namespace;
                options.Database = _database;
                options.Username = Username;
                options.Password = Password;
                options.Schema.For<VersionedVaultContract>().Identity(contract => contract.Id).SetSchemaMode(SchemaMode.Flexible);
                options.Schema.For<VaultRaceSibling>().Identity(sibling => sibling.Id).SetSchemaMode(SchemaMode.Flexible);
                if (listener is not null)
                {
                    options.Listeners.Add(listener);
                }
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
                    {
                        return;
                    }

                    lastFailure = new InvalidOperationException("Unexpected server version.");
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
                "The isolated SurrealDB expected-version test fixture did not become ready. " +
                $"Last failure type: {lastFailure?.GetType().Name ?? "none"}.");
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
            }) ?? throw new InvalidOperationException("Could not start Docker for the expected-version race fixture.");

            try
            {
                var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
                var standardError = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                var output = await standardOutput;
                var error = await standardError;
                if (!tolerateFailure && process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker command failed ({process.ExitCode}): {error}{output}");
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The docker CLI exited between HasExited and Kill.
                }

                throw;
            }
        }
    }
}
