namespace Dali.IntegrationTests;

/// <summary>
/// Test harness for integration tests against a real (remote) SurrealDB instance
/// running in unauthenticated mode (--unauthenticated).
/// Uses <see cref="StoreOptions.Endpoint"/>, <see cref="StoreOptions.Namespace"/>,
/// and <see cref="StoreOptions.Database"/> directly — no username/password.
/// Connection settings come from environment variables with sensible defaults.
/// </summary>
public static class TestHarness
{
    public static string Endpoint =>
        Environment.GetEnvironmentVariable("DALI_TEST_ENDPOINT") ?? "ws://localhost:8000";

    public static string Namespace =>
        Environment.GetEnvironmentVariable("DALI_TEST_NS") ?? "test";

    public static string Database =>
        Environment.GetEnvironmentVariable("DALI_TEST_DB") ?? "test";

    /// <summary>
    /// Check whether a remote SurrealDB endpoint is reachable.
    /// Returns false if the connection fails (e.g., no server running).
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var store = Documents.For(o =>
            {
                o.Endpoint = Endpoint;
                o.Namespace = Namespace;
                o.Database = Database;
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
            o.Endpoint = Endpoint;
            o.Namespace = Namespace;
            o.Database = Database;
            configure?.Invoke(o);
        });

        await store.InitializeAsync();
        return store;
    }
}
