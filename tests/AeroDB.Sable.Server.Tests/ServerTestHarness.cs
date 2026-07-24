using System.Text.RegularExpressions;

namespace AeroDB.Sable.Server.Tests;

internal static partial class ServerTestHarness
{
    private static readonly Version MinimumServerVersion = new(3, 2);

    public static string Endpoint =>
        Environment.GetEnvironmentVariable("AERODB_SERVER_TEST_ENDPOINT") ?? "ws://localhost:8000/rpc";

    public static string Namespace =>
        Environment.GetEnvironmentVariable("AERODB_SERVER_TEST_NAMESPACE") ?? "sable_capability";

    public static string? Username =>
        Environment.GetEnvironmentVariable("AERODB_SERVER_TEST_USERNAME");

    public static string? Password =>
        Environment.GetEnvironmentVariable("AERODB_SERVER_TEST_PASSWORD");

    public static async Task<IDocumentStore> CreateStoreAsync(
        string database,
        Action<StoreOptions>? configure = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var store = Documents.For(options =>
        {
            options.Endpoint = Endpoint;
            options.Namespace = Namespace;
            options.Database = database;
            options.Username = Username;
            options.Password = Password;
            configure?.Invoke(options);
        });

        try
        {
            await store.InitializeAsync(ct);
            var rawVersion = await store.GetVersionAsync(ct);
            var serverVersion = ParseVersion(rawVersion);

            if (serverVersion is null || serverVersion < MinimumServerVersion)
            {
                await store.DisposeAsync();
                throw new InvalidOperationException(
                    $"Sable server integration tests require SurrealDB {MinimumServerVersion}+; " +
                    $"'{Endpoint}' reported '{rawVersion ?? "an unknown version"}'.");
            }

            return store;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            await store.DisposeAsync();
            throw new InvalidOperationException(
                $"Sable server integration tests could not connect to SurrealDB at '{Endpoint}'. " +
                "Start the pinned server with 'docker compose up -d' or configure the " +
                "AERODB_SERVER_TEST_* environment variables.",
                exception);
        }
    }

    private static Version? ParseVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var match = VersionPattern().Match(rawVersion);
        return match.Success && Version.TryParse(match.Value, out var version)
            ? version
            : null;
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionPattern();
}
