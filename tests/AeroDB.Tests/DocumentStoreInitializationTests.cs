using AeroDB.Sable;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net.Exceptions.Embedded;
using TUnit.Core;

namespace AeroDB.Tests;

[NotInParallel]
public sealed class DocumentStoreInitializationTests
{
    [Test]
    public async Task Concurrent_initialize_call_waits_for_active_initialization()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var store = Documents.For(options =>
        {
            options.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            options.AsyncConfigurators.Add(new BlockingConfigurator(entered, release));
        });

        var firstInitialization = store.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var concurrentInitialization = store.InitializeAsync();

        try
        {
            concurrentInitialization.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(firstInitialization, concurrentInitialization);
    }

    [Test]
    public async Task Failed_initialization_releases_embedded_database_within_bounded_retry()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"aerodb_failed_initialization_{Guid.NewGuid():N}");
        var store = Documents.For(options =>
        {
            options.ClientFactory = () => new SurrealDbKvClient(dataPath);
            options.Schema.For<FailureDocument>()
                .FullTextIndex(x => x.Name, "missing_failure_analyzer");
        });

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => store.InitializeAsync());
            Should.Throw<InvalidOperationException>(() => _ = store.Client);

            await using var nextClient = new SurrealDbKvClient(dataPath);
            Func<Task> connectAgain = () => ConnectAfterNativeCleanupAsync(nextClient);

            await connectAgain.ShouldNotThrowAsync();
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task ConnectAfterNativeCleanupAsync(SurrealDbKvClient client)
    {
        // The pinned embedded SDK removes its native engine on a background Rust
        // task. DisposeAsync completes before that task releases SurrealKV's lock.
        // Retry only this transient lock condition, with at most 1.575s of backoff.
        var delay = TimeSpan.FromMilliseconds(25);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.Connect();
                return;
            }
            catch (SurrealDbEmbeddedException exception)
                when (attempt < 7
                    && (exception.Message.Contains("locked a portion of the file", StringComparison.OrdinalIgnoreCase)
                        || exception.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                        || exception.Message.Contains("already locked by another process", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(delay);
                delay += delay;
            }
        }
    }

    private sealed class BlockingConfigurator(
        TaskCompletionSource entered,
        TaskCompletionSource release) : IAsyncConfigureAeroDB
    {
        public async Task ConfigureAsync(StoreOptions options, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
    }

    private sealed class FailureDocument
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
