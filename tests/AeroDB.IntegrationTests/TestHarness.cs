using AeroDB.Sable;
using SurrealDb.Net;

namespace AeroDB.IntegrationTests;

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
        Environment.GetEnvironmentVariable("AeroDB_TEST_ENDPOINT") ?? "ws://localhost:8000";

    public static string Namespace =>
        Environment.GetEnvironmentVariable("AeroDB_TEST_NS") ?? "test";

    public static string Database =>
        Environment.GetEnvironmentVariable("AeroDB_TEST_DB") ?? "test";

    /// <summary>
    /// Require a reachable remote SurrealDB endpoint. An unavailable server must
    /// fail integration tests instead of letting them pass without assertions.
    /// </summary>
    public static async Task EnsureAvailableAsync()
    {
        try
        {
            // A connectivity probe must not initialize a shared namespace/schema:
            // concurrent tests would contend on its catalog writes.
            await using var client = new SurrealDbClient(Endpoint);
            await client.Connect();
            await client.Version();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Integration tests require an available SurrealDB server at {Endpoint}.",
                exception);
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
            // Live subscriptions and fixed record IDs must remain isolated
            // from other tests and previous runs on a persistent test server.
            o.Namespace = $"{Namespace}_{Guid.NewGuid():N}";
            o.Database = Database;
            // LIVE SELECT requires an existing table before any records are written.
            o.Schema.For<TestPerson>().SetSchemaMode(SchemaMode.Flexible);
            configure?.Invoke(o);
        });

        await store.InitializeAsync();
        return store;
    }
}
