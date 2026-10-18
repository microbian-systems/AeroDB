using System.Diagnostics;
using AeroDB.Sable;

namespace AeroDB.Tests;

/// <summary>
/// Disposable authenticated SurrealDB coordinator for Wolverine server regressions.
/// It intentionally uses a random loopback port and a unique container per test.
/// </summary>
internal sealed class AuthenticatedWolverineFixture : IAsyncDisposable
{
    private const string Username = "aerodb_node_coordination";
    private const string Password = "aerodb-node-coordination-local-only";
    private readonly string _containerName;
    private readonly int _port;

    private AuthenticatedWolverineFixture(string containerName, int port)
    {
        _containerName = containerName;
        _port = port;
    }

    public static async Task<AuthenticatedWolverineFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new AuthenticatedWolverineFixture(
            "aerodb-node-coordination-" + Guid.NewGuid().ToString("N"),
            GetAvailableLoopbackPort());
        var arguments =
            $"run -d --rm --name {fixture._containerName} --label aerodb.testfixture=node-coordination " +
            $"-p 127.0.0.1:{fixture._port}:8000 surrealdb/surrealdb:v3.2.4 " +
            $"start --user {Username} --pass {Password} memory";

        await RunDockerAsync(arguments, cancellationToken);
        try
        {
            await fixture.WaitUntilReadyAsync(cancellationToken);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task<IDocumentStore> CreateInitializedStoreAsync(
        CancellationToken cancellationToken,
        Action<StoreOptions>? configure = null)
        => await CreateInitializedStoreAsync(
            "wolverine_coordinator",
            "main",
            cancellationToken,
            configure);

    /// <summary>
    /// Creates and initializes an isolated fixed-scope document store. Server
    /// tests use this for tenant stores that must not inherit the coordinator
    /// namespace or database.
    /// </summary>
    public async Task<IDocumentStore> CreateInitializedStoreAsync(
        string @namespace,
        string database,
        CancellationToken cancellationToken,
        Action<StoreOptions>? configure = null)
    {
        await EnsureScopeAsync(@namespace, database, cancellationToken);
        var store = CreateStore(@namespace, database, configure);
        try
        {
            await store.InitializeAsync(cancellationToken);
            return store;
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates an uninitialized isolated fixed-scope store for factories that
    /// own initialization as part of their resource lifecycle.
    /// </summary>
    public async Task<IDocumentStore> CreateUninitializedStoreAsync(
        string @namespace,
        string database,
        CancellationToken cancellationToken,
        Action<StoreOptions>? configure = null)
    {
        await EnsureScopeAsync(@namespace, database, cancellationToken);
        return CreateStore(@namespace, database, configure);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await RunDockerAsync($"rm -f {_containerName}", timeout.Token, tolerateFailure: true);
    }

    public static int GetAvailableLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task EnsureScopeAsync(
        string @namespace,
        string database,
        CancellationToken cancellationToken)
    {
        await using var bootstrap = CreateStore("bootstrap", "bootstrap");
        await bootstrap.InitializeAsync(cancellationToken);
        await using var session = await bootstrap.LightweightSessionAsync(cancellationToken);
        await session.ExecuteSqlAsync($"DEFINE NAMESPACE IF NOT EXISTS `{@namespace}`;", null, cancellationToken);
        await session.ExecuteSqlAsync($"USE NS `{@namespace}`;", null, cancellationToken);
        await session.ExecuteSqlAsync($"DEFINE DATABASE IF NOT EXISTS `{database}`;", null, cancellationToken);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Exception? lastFailure = null;

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var store = CreateStore("bootstrap", "bootstrap");
                await store.InitializeAsync(timeout.Token);
                if ((await store.GetVersionAsync(timeout.Token))?.Contains("3.2.4", StringComparison.Ordinal) == true)
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

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"The isolated authenticated SurrealDB fixture did not become ready. Last failure: {lastFailure?.GetType().Name ?? "none"}.");
    }

    private IDocumentStore CreateStore(
        string @namespace = "wolverine_coordinator",
        string database = "main",
        Action<StoreOptions>? configure = null) =>
        Documents.For(options =>
        {
            options.Endpoint = $"ws://127.0.0.1:{_port}/rpc";
            options.Namespace = @namespace;
            options.Database = database;
            options.Username = Username;
            options.Password = Password;
            configure?.Invoke(options);
        });

    private static async Task RunDockerAsync(string arguments, CancellationToken cancellationToken, bool tolerateFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start Docker for the isolated SurrealDB fixture.");

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (!tolerateFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"Docker command failed ({process.ExitCode}): {error}{output}");
    }
}
