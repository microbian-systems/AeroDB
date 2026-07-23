using AeroDB.Sable;
using SurrealDb.Embedded.SurrealKv;
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
    public async Task Failed_initialization_releases_embedded_database_immediately()
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

            await using var nextClient = new SurrealDbKvClient(dataPath);
            Func<Task> connectAgain = () => nextClient.Connect();

            await connectAgain.ShouldNotThrowAsync();
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, recursive: true);
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
