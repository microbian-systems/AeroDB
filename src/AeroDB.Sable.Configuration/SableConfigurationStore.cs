using System.Security.Cryptography;
using System.Text;
using SurrealDb.Embedded.SurrealKv;
using SurrealDb.Net;
using SurrealDb.Net.Exceptions.Embedded;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable.Configuration;

/// <summary>
/// Persistent embedded SurrealKV store whose values are protected by Sable
/// envelope encryption before crossing the database-client boundary.
/// </summary>
public sealed class SableConfigurationStore : ISableConfigurationStore
{
    internal const string TableName = "sable_configuration_entry";
    internal const string StorageField = "value";
    internal const string CodecId = "utf8-string-v1";
    internal const int MaximumKeyCharacters = 512;

    private readonly ValidatedSableConfigurationOptions _options;
    private readonly IKeyWrappingProvider _keyWrappingProvider;
    private readonly ISableDataProtectionProvider _dataProtectionProvider;
    private readonly SharedEmbeddedClient _sharedClient;
    private bool _disposed;

    /// <summary>Creates a configuration store from validated bootstrap options.</summary>
    public SableConfigurationStore(SableConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
        _keyWrappingProvider = _options.KeyWrappingProviderFactory()
            ?? throw new SableConfigurationException(
                "The key-wrapping provider factory returned null.");
        try
        {
            _dataProtectionProvider = new AeadEnvelopeDataProtectionProvider(
                _keyWrappingProvider,
                _options.MaximumValueBytes);
            _sharedClient = EmbeddedClientRegistry.Acquire(_options);
        }
        catch
        {
            if (_keyWrappingProvider is IDisposable disposable)
                disposable.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SableConfigurationEntry?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var validatedKey = ValidateKey(key);
        var recordId = CreateRecordId(validatedKey);

        return await WithClientAsync(
            async (client, ct) =>
            {
                var record = await client
                    .Select<StoredConfigurationRecord>(
                        new StringRecordId($"{TableName}:{recordId}"),
                        ct)
                    .ConfigureAwait(false);
                return record is null
                    ? null
                    : await DecryptAsync(record, recordId, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await WithClientAsync(
            async (client, ct) =>
            {
                var records = await client
                    .Select<StoredConfigurationRecord>(TableName, ct)
                    .ConfigureAwait(false);
                var values = new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var record in records)
                {
                    var key = ValidateKey(record.Key);
                    var recordId = GetRecordId(record);
                    var entry = await DecryptAsync(record, recordId, ct)
                        .ConfigureAwait(false);
                    if (!values.TryAdd(key, entry.Value))
                        throw new SableConfigurationDuplicateKeyException(key);
                }

                return values;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var validatedKey = ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        var plaintext = Encoding.UTF8.GetBytes(value);
        if (plaintext.Length > _options.MaximumValueBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SableConfigurationException(
                $"Configuration value for key '{validatedKey}' exceeds the configured " +
                $"maximum of {_options.MaximumValueBytes} UTF-8 bytes.");
        }

        var recordId = CreateRecordId(validatedKey);
        try
        {
            var context = CreateEncryptionContext(recordId);
            var envelope = await _dataProtectionProvider
                .ProtectAsync(
                    plaintext,
                    context,
                    _options.EncryptionAlgorithm,
                    cancellationToken)
                .ConfigureAwait(false);

            var record = new StoredConfigurationRecord
            {
                Key = validatedKey,
                Value = StoredEncryptedEnvelope.FromEnvelope(envelope),
                UpdatedAt = DateTime.UtcNow
            };

            await WithClientAsync(
                async (client, ct) =>
                {
                    await client
                        .Upsert<StoredConfigurationRecord, StoredConfigurationRecord>(
                            new StringRecordId($"{TableName}:{recordId}"),
                            record,
                            ct)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var recordId = CreateRecordId(ValidateKey(key));

        return await WithClientAsync(
            (client, ct) => client.Delete(
                new StringRecordId($"{TableName}:{recordId}"),
                ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_keyWrappingProvider is IDisposable disposable)
            disposable.Dispose();
        EmbeddedClientRegistry.Release(_sharedClient);
    }

    internal static string CreateRecordId(string key)
    {
        var normalized = key.ToUpperInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<T> WithClientAsync<T>(
        Func<ISurrealDbClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _sharedClient.OperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sharedClient.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await operation(_sharedClient.Client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sharedClient.OperationLock.Release();
        }
    }

    private async Task<SableConfigurationEntry> DecryptAsync(
        StoredConfigurationRecord record,
        string recordId,
        CancellationToken cancellationToken)
    {
        ValidateRecordBinding(record, recordId);
        var envelope = record.Value?.ToEnvelope()
            ?? throw new SableConfigurationException(
                $"Configuration entry '{record.Key}' has no encrypted value.");
        using var plaintext = await _dataProtectionProvider
            .UnprotectAsync(
                envelope,
                CreateEncryptionContext(recordId),
                cancellationToken)
            .ConfigureAwait(false);
        return new SableConfigurationEntry(
            ValidateKey(record.Key),
            Encoding.UTF8.GetString(plaintext.Memory.Span),
            new DateTimeOffset(DateTime.SpecifyKind(record.UpdatedAt, DateTimeKind.Utc)));
    }

    private static void ValidateRecordBinding(
        StoredConfigurationRecord record,
        string recordId)
    {
        var expectedRecordId = CreateRecordId(ValidateKey(record.Key));
        if (!string.Equals(expectedRecordId, recordId, StringComparison.Ordinal))
        {
            throw new SableConfigurationException(
                $"Configuration entry '{record.Key}' is stored under an invalid record ID.");
        }
    }

    private EncryptionContext CreateEncryptionContext(string recordId) =>
        new(
            _options.Namespace,
            _options.Database,
            TableName,
            recordId,
            TenantId: null,
            StorageField,
            CodecId);

    private static string GetRecordId(StoredConfigurationRecord record)
    {
        if (record.Id is null)
        {
            throw new SableConfigurationException(
                $"Configuration entry '{record.Key}' has no stable record ID.");
        }

        try
        {
            return record.Id.DeserializeId<string>();
        }
        catch (Exception exception)
        {
            throw new SableConfigurationException(
                $"Configuration entry '{record.Key}' has an invalid record ID.",
                exception);
        }
    }

    private static string ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key))
            throw new SableConfigurationException("Configuration keys cannot be empty or whitespace.");
        if (!string.Equals(key, key.Trim(), StringComparison.Ordinal))
        {
            throw new SableConfigurationException(
                $"Configuration key '{key}' cannot have leading or trailing whitespace.");
        }
        if (key.Length > MaximumKeyCharacters)
        {
            throw new SableConfigurationException(
                $"Configuration key exceeds the maximum length of {MaximumKeyCharacters} characters.");
        }
        if (key.Contains('\0'))
            throw new SableConfigurationException("Configuration keys cannot contain null characters.");
        return key;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class EmbeddedClientRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SharedEmbeddedClient> Clients =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    internal static SharedEmbeddedClient Acquire(
        ValidatedSableConfigurationOptions options)
    {
        lock (Sync)
        {
            if (Clients.TryGetValue(options.DatabasePath, out var existing))
            {
                if (!string.Equals(existing.Namespace, options.Namespace, StringComparison.Ordinal)
                    || !string.Equals(existing.Database, options.Database, StringComparison.Ordinal))
                {
                    throw new SableConfigurationException(
                        $"Embedded configuration path '{options.DatabasePath}' is already open " +
                        "for a different SurrealDB namespace or database.");
                }

                existing.AddReference();
                return existing;
            }

            var created = new SharedEmbeddedClient(
                options.DatabasePath,
                options.Namespace,
                options.Database);
            Clients.Add(options.DatabasePath, created);
            return created;
        }
    }

    internal static void Release(SharedEmbeddedClient client)
    {
        lock (Sync)
        {
            if (client.ReleaseReference() != 0)
                return;

            Clients.Remove(client.DatabasePath);
            client.Dispose();
        }
    }
}

internal sealed class SharedEmbeddedClient : IDisposable
{
    private const string InitializeSchemaSurrealQl =
        """
        DEFINE TABLE IF NOT EXISTS sable_configuration_entry SCHEMAFULL;
        DEFINE FIELD IF NOT EXISTS key ON TABLE sable_configuration_entry TYPE string;
        DEFINE FIELD IF NOT EXISTS value ON TABLE sable_configuration_entry TYPE object FLEXIBLE;
        DEFINE FIELD IF NOT EXISTS updated_at ON TABLE sable_configuration_entry TYPE datetime;
        """;

    private int _references = 1;
    private bool _initialized;
    private bool _disposed;

    internal SharedEmbeddedClient(
        string databasePath,
        string @namespace,
        string database)
    {
        DatabasePath = databasePath;
        Namespace = @namespace;
        Database = database;
        Client = new SurrealDbKvClient(databasePath);
    }

    internal string DatabasePath { get; }
    internal string Namespace { get; }
    internal string Database { get; }
    internal SurrealDbKvClient Client { get; }
    internal SemaphoreSlim OperationLock { get; } = new(1, 1);

    internal void AddReference()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        checked
        {
            _references++;
        }
    }

    internal int ReleaseReference() => --_references;

    internal async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return;

        await ConnectWithLockRetryAsync(cancellationToken).ConfigureAwait(false);
        await Client.Use(Namespace, Database, cancellationToken).ConfigureAwait(false);
        SurrealDbResponse response = await Client
            .RawQuery(InitializeSchemaSurrealQl, parameters: null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureAllOks();
        _initialized = true;
    }

    private async Task ConnectWithLockRetryAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 6;
        var delay = TimeSpan.FromMilliseconds(25);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Client.Connect(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SurrealDbEmbeddedException exception)
                when (attempt < maximumAttempts && IsTransientFileLock(exception))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay += delay;
            }
        }
    }

    private static bool IsTransientFileLock(SurrealDbEmbeddedException exception) =>
        exception.Message.Contains("locked a portion of the file", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        OperationLock.Dispose();
    }
}
