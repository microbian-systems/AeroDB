using System.Security.Cryptography;
using System.Text.Json;
using AeroDB.Sable;
using AeroDB.Sable.Configuration;
using Dahomey.Cbor.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net.Exceptions.Embedded;
using SurrealDb.Net.Models.Response;
using TUnit.Core;

namespace AeroDB.Sable.Configuration.Tests;

[NotInParallel]
public sealed class SableConfigurationStoreTests
{
    [Test]
    public async Task Set_get_delete_and_reopen_round_trip()
    {
        using var environment = TestEnvironment.Create();

        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
        {
            await writer.SetAsync("ConnectionStrings:Primary", "server=primary;password=s3cret");

            var entry = await writer.GetAsync("connectionstrings:PRIMARY");
            entry.ShouldNotBeNull();
            entry!.Key.ShouldBe("ConnectionStrings:Primary");
            entry.Value.ShouldBe("server=primary;password=s3cret");
            entry.UpdatedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        }

        using (var reader = new SableConfigurationStore(environment.CreateOptions()))
        {
            var persisted = await reader.GetAsync("CONNECTIONSTRINGS:PRIMARY");
            persisted.ShouldNotBeNull();
            persisted!.Value.ShouldBe("server=primary;password=s3cret");

            (await reader.DeleteAsync("connectionstrings:primary")).ShouldBeTrue();
            (await reader.DeleteAsync("connectionstrings:primary")).ShouldBeFalse();
            (await reader.GetAsync("connectionstrings:primary")).ShouldBeNull();
        }
    }

    [Test]
    public async Task Case_insensitive_upsert_has_one_logical_record()
    {
        using var environment = TestEnvironment.Create();
        using var store = new SableConfigurationStore(environment.CreateOptions());

        await store.SetAsync("Features:Payments:ApiKey", "first");
        await store.SetAsync("features:payments:apikey", "second");

        var snapshot = await store.LoadAsync();
        snapshot.Count.ShouldBe(1);
        snapshot["FEATURES:PAYMENTS:APIKEY"].ShouldBe("second");
    }

    [Test]
    public async Task Stored_record_contains_an_envelope_not_plaintext()
    {
        using var environment = TestEnvironment.Create();
        const string plaintext = "plaintext-must-not-cross-the-database-boundary";

        using (var store = new SableConfigurationStore(environment.CreateOptions()))
            await store.SetAsync("Secrets:ApiKey", plaintext);

        await using var client = new SurrealDbKvClient(environment.DatabasePath);
        await ConnectWithLockRetryAsync(client);
        await client.Use("aerodb", "configuration");
        SurrealDbResponse response = await client.RawQuery(
            """
            SELECT key, value, updated_at
            FROM sable_configuration_entry;
            """,
            parameters: null);
        var records = response.GetValue<List<RawConfigurationRecord>>(0);
        var serialized = JsonSerializer.Serialize(records);

        serialized.ShouldNotContain(plaintext);
        records.ShouldNotBeNull();
        records!.Single().Value.Ciphertext.ShouldNotBeEmpty();
        records.Single().Value.WrappedKeyCiphertext.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Wrong_key_fails_closed()
    {
        using var environment = TestEnvironment.Create();
        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Secrets:ApiKey", "classified");

        var wrongKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var reader = new SableConfigurationStore(
                environment.CreateOptions(wrongKey));

            await Should.ThrowAsync<SableEncryptionAuthenticationException>(
                () => reader.LoadAsync());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongKey);
        }
    }

    [Test]
    public async Task Tampered_clear_key_record_binding_fails_closed()
    {
        using var environment = TestEnvironment.Create();
        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Secrets:ApiKey", "classified");

        await using (var client = new SurrealDbKvClient(environment.DatabasePath))
        {
            await ConnectWithLockRetryAsync(client);
            await client.Use("aerodb", "configuration");
            var response = await client.RawQuery(
                """
                UPDATE sable_configuration_entry
                SET key = $tampered_key;
                """,
                new Dictionary<string, object?>
                {
                    ["tampered_key"] = "Tampered:ApiKey"
                });
            response.EnsureAllOks();
        }

        using var reader = new SableConfigurationStore(environment.CreateOptions());
        var exception = await Should.ThrowAsync<SableConfigurationException>(
            () => reader.LoadAsync());
        exception.Message.ShouldContain("invalid record ID");
        exception.Message.ShouldNotContain("classified");
    }

    [Test]
    public async Task Configuration_provider_preserves_json_hierarchy_and_last_provider_wins()
    {
        using var environment = TestEnvironment.Create();
        var jsonPath = Path.Combine(environment.RootPath, "appsettings.json");
        await File.WriteAllTextAsync(
            jsonPath,
            """
            {
              "Payments": {
                "Endpoint": "https://json.example",
                "ApiKey": "json-value"
              }
            }
            """);

        using (var store = new SableConfigurationStore(environment.CreateOptions()))
            await store.SetAsync("Payments:ApiKey", "sable-value");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(jsonPath, optional: false, reloadOnChange: false)
            .AddSableConfiguration(environment.CreateOptions())
            .Build();

        configuration["Payments:Endpoint"].ShouldBe("https://json.example");
        configuration["Payments:ApiKey"].ShouldBe("sable-value");

        var bound = configuration.GetRequiredSection("Payments").Get<PaymentOptions>();
        bound.ShouldNotBeNull();
        bound!.Endpoint.ShouldBe("https://json.example");
        bound.ApiKey.ShouldBe("sable-value");
        DisposeProviders(configuration);
    }

    [Test]
    public async Task Explicit_refresh_updates_snapshot_and_signals_change()
    {
        using var environment = TestEnvironment.Create();
        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Feature:Enabled", "false");

        var configuration = new ConfigurationBuilder()
            .AddSableConfiguration(environment.CreateOptions())
            .Build();
        var provider = configuration.Providers
            .OfType<SableConfigurationProvider>()
            .Single();
        var changed = false;
        using var registration = configuration
            .GetReloadToken()
            .RegisterChangeCallback(_ => changed = true, null);

        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Feature:Enabled", "true");

        (await provider.RefreshAsync()).ShouldBeTrue();
        configuration["Feature:Enabled"].ShouldBe("true");
        changed.ShouldBeTrue();
        (await provider.RefreshAsync()).ShouldBeFalse();
        DisposeProviders(configuration);
    }

    [Test]
    public async Task Explicit_refresh_updates_options_monitor()
    {
        using var environment = TestEnvironment.Create();
        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Feature:Enabled", "false");

        var configuration = new ConfigurationBuilder()
            .AddSableConfiguration(environment.CreateOptions())
            .Build();
        var provider = configuration.Providers
            .OfType<SableConfigurationProvider>()
            .Single();

        var services = new ServiceCollection();
        services.AddOptions<FeatureOptions>()
            .Bind(configuration.GetRequiredSection("Feature"));
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<FeatureOptions>>();
        monitor.CurrentValue.Enabled.ShouldBeFalse();

        var notified = false;
        using var registration = monitor.OnChange(options => notified = options.Enabled);
        using (var writer = new SableConfigurationStore(environment.CreateOptions()))
            await writer.SetAsync("Feature:Enabled", "true");

        (await provider.RefreshAsync()).ShouldBeTrue();
        monitor.CurrentValue.Enabled.ShouldBeTrue();
        notified.ShouldBeTrue();
        DisposeProviders(configuration);
    }

    [Test]
    public async Task Configuration_indexer_write_is_rejected()
    {
        using var environment = TestEnvironment.Create();
        var configuration = new ConfigurationBuilder()
            .AddSableConfiguration(environment.CreateOptions())
            .Build();

        Should.Throw<SableConfigurationReadOnlyException>(
            () => configuration["Feature:Enabled"] = "true");

        DisposeProviders(configuration);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Invalid_bootstrap_and_input_are_rejected_before_persistence()
    {
        var missingPath = new SableConfigurationOptions()
            .UseKeyWrappingProvider(() =>
                new AesGcmKeyWrappingProvider(
                    RandomNumberGenerator.GetBytes(32),
                    "test-key"));
        Should.Throw<SableConfigurationException>(
            () => new SableConfigurationStore(missingPath));

        using var environment = TestEnvironment.Create();
        using var store = new SableConfigurationStore(environment.CreateOptions());
        await Should.ThrowAsync<SableConfigurationException>(
            () => store.SetAsync(" whitespace ", "value"));
        await Should.ThrowAsync<ArgumentNullException>(
            () => store.SetAsync("Valid", null!));
    }

    private sealed class PaymentOptions
    {
        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public sealed class FeatureOptions
    {
        public bool Enabled { get; set; }
    }

    private sealed class RawConfigurationRecord
    {
        [CborProperty("key")]
        public string Key { get; set; } = string.Empty;

        [CborProperty("value")]
        public RawEncryptedEnvelope Value { get; set; } = new();

        [CborProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class RawEncryptedEnvelope
    {
        [CborProperty("ciphertext")]
        public byte[] Ciphertext { get; set; } = [];

        [CborProperty("wrapped_key_ciphertext")]
        public byte[] WrappedKeyCiphertext { get; set; } = [];
    }

    private static void DisposeProviders(IConfigurationRoot configuration)
    {
        foreach (var provider in configuration.Providers.OfType<IDisposable>())
            provider.Dispose();
    }

    private static async Task ConnectWithLockRetryAsync(SurrealDbKvClient client)
    {
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
                    && exception.Message.Contains(
                        "locked a portion of the file",
                        StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(delay);
                delay += delay;
            }
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly byte[] _key;

        private TestEnvironment(string rootPath, byte[] key)
        {
            RootPath = rootPath;
            DatabasePath = Path.Combine(rootPath, "configuration.skv");
            _key = key;
        }

        internal string RootPath { get; }
        internal string DatabasePath { get; }

        internal static TestEnvironment Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"aerodb_sable_configuration_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TestEnvironment(root, RandomNumberGenerator.GetBytes(32));
        }

        internal SableConfigurationOptions CreateOptions(
            byte[]? key = null,
            string keyId = "test-key")
        {
            var keyMaterial = key ?? _key;
            return new SableConfigurationOptions
            {
                DatabasePath = DatabasePath
            }.UseKeyWrappingProvider(
                () => new AesGcmKeyWrappingProvider(keyMaterial, keyId, "tests"));
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_key);
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
