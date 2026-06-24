namespace Dali.Tests;

/// <summary>
/// Test harness for integration tests against a real (remote) SurrealDB instance.
/// Uses <see cref="SurrealDb.Net.SurrealDbClient"/> (WebSocket) instead of the in-memory engine.
/// Connection settings come from environment variables with sensible defaults.
/// </summary>
public static class TestHarnessRemote
{
    public static string Endpoint =>
        Environment.GetEnvironmentVariable("DALI_TEST_ENDPOINT") ?? "ws://localhost:8000";

    public static string Namespace =>
        Environment.GetEnvironmentVariable("DALI_TEST_NS") ?? "test";

    public static string Database =>
        Environment.GetEnvironmentVariable("DALI_TEST_DB") ?? "test";

    public static string Username =>
        Environment.GetEnvironmentVariable("DALI_TEST_USER") ?? "root";

    public static string Password =>
        Environment.GetEnvironmentVariable("DALI_TEST_PASS") ?? "root";

    /// <summary>
    /// Check whether a remote SurrealDB endpoint is reachable.
    /// Returns false if the connection fails (e.g., no server running).
    /// </summary>
    public static async Task<bool> IsRemoteAvailableAsync()
    {
        try
        {
            var store = Documents.For(o =>
            {
                o.Connection(Endpoint, Namespace, Database, Username, Password);
            });

            await store.InitializeAsync();
            await store.DisposeAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Create a store connected to the remote SurrealDB instance.
    /// Throws if the instance is unreachable.
    /// </summary>
    public static async Task<IDocumentStore> CreateStoreAsync(Action<StoreOptions>? configure = null)
    {
        var store = Documents.For(o =>
        {
            o.Connection(Endpoint, Namespace, Database, Username, Password);
            configure?.Invoke(o);
        });

        await store.InitializeAsync();
        return store;
    }
}
