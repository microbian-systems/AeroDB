using System.Text.RegularExpressions;

namespace AeroDB.Sable.Configuration;

/// <summary>
/// Options for the embedded encrypted Sable configuration store.
/// Bootstrap values must come from outside the store itself.
/// </summary>
public sealed partial class SableConfigurationOptions
{
    private const int DefaultMaximumValueBytes = 64 * 1024;
    private int _maximumValueBytes = DefaultMaximumValueBytes;
    private Func<IKeyWrappingProvider>? _keyWrappingProviderFactory;

    /// <summary>Persistent SurrealKV database path. This value is required.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>SurrealDB namespace used by the configuration store.</summary>
    public string Namespace { get; set; } = "aerodb";

    /// <summary>SurrealDB database used by the configuration store.</summary>
    public string Database { get; set; } = "configuration";

    /// <summary>Reversible authenticated-encryption algorithm used for new writes.</summary>
    public EncryptionAlgorithm EncryptionAlgorithm { get; set; } =
        EncryptionAlgorithm.Aes256Gcm;

    /// <summary>Maximum UTF-8 encoded plaintext size accepted for one value.</summary>
    public int MaximumValueBytes
    {
        get => _maximumValueBytes;
        set => _maximumValueBytes = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Configures a factory for the wrapping-key provider. The store owns and
    /// disposes each provider returned by this factory.
    /// </summary>
    public SableConfigurationOptions UseKeyWrappingProvider(
        Func<IKeyWrappingProvider> providerFactory)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        _keyWrappingProviderFactory = providerFactory;
        return this;
    }

    /// <summary>
    /// Loads a raw 32-byte or Base64-encoded 32-byte KEK from a mounted file.
    /// The host remains responsible for the file's ownership and ACL.
    /// </summary>
    public SableConfigurationOptions UseMountedKeyFile(
        string path,
        string keyId,
        string providerId = "mounted-file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        _keyWrappingProviderFactory = () =>
            AesGcmKeyWrappingProvider.FromMountedFile(path, keyId, providerId);
        return this;
    }

    internal ValidatedSableConfigurationOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new SableConfigurationException(
                $"{nameof(DatabasePath)} is required for the persistent embedded configuration store.");
        }

        if (!NamePattern().IsMatch(Namespace))
            throw InvalidName(nameof(Namespace), Namespace);
        if (!NamePattern().IsMatch(Database))
            throw InvalidName(nameof(Database), Database);

        if (EncryptionAlgorithm is not (
            EncryptionAlgorithm.Aes256Gcm
            or EncryptionAlgorithm.ChaCha20Poly1305))
        {
            throw new SableConfigurationException(
                $"Encryption algorithm '{EncryptionAlgorithm}' is not supported.");
        }

        if (_keyWrappingProviderFactory is null)
        {
            throw new SableConfigurationException(
                "A key-wrapping provider is required. Configure UseMountedKeyFile(...) " +
                "or UseKeyWrappingProvider(...).");
        }

        return new ValidatedSableConfigurationOptions(
            Path.GetFullPath(DatabasePath),
            Namespace,
            Database,
            EncryptionAlgorithm,
            MaximumValueBytes,
            _keyWrappingProviderFactory);
    }

    private static SableConfigurationException InvalidName(string property, string value) =>
        new(
            $"{property} value '{value}' is invalid. Use only ASCII letters, digits, " +
            "underscores, or hyphens.");

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

internal sealed record ValidatedSableConfigurationOptions(
    string DatabasePath,
    string Namespace,
    string Database,
    EncryptionAlgorithm EncryptionAlgorithm,
    int MaximumValueBytes,
    Func<IKeyWrappingProvider> KeyWrappingProviderFactory);
