namespace AeroDB.Sable;

/// <summary>Application-layer field-encryption configuration.</summary>
public sealed class SableDataEncryptionOptions
{
    private int _maximumEnvelopeCiphertextBytes = 1024 * 1024;

    /// <summary>
    /// Provider used by mapped encrypted fields. Encrypted writes and reads fail
    /// closed when no provider is configured.
    /// </summary>
    public ISableDataProtectionProvider? Provider { get; set; }

    /// <summary>
    /// Separately keyed provider used by explicitly mapped blind indexes.
    /// Blind-index keys must never be reused as encryption KEKs or DEKs.
    /// </summary>
    public IBlindIndexTokenProvider? BlindIndexProvider { get; set; }

    /// <summary>
    /// Explicit assurance required when <see cref="StoreOptions.ClientFactory"/>
    /// supplies the SurrealDB client for protected mappings. Set this only when
    /// that client has query-value and CBOR serialization logging disabled.
    /// Sable-created clients do not attach a client logger.
    /// </summary>
    public bool ExternalClientDisablesProtectedDataLogging { get; set; }

    /// <summary>
    /// Explicit assurance that an externally supplied client protects database
    /// traffic in transit (for example TLS or an in-process transport).
    /// Required before password candidates may be sent to that client.
    /// </summary>
    public bool ExternalClientUsesProtectedTransport { get; set; }

    /// <summary>
    /// Development-only opt-in for hashing over an HTTP/WS loopback endpoint.
    /// Never enables insecure non-loopback transport.
    /// </summary>
    public bool AllowInsecureLoopbackHashing { get; set; }

    /// <summary>
    /// Maximum ciphertext accepted from a stored field envelope before Base64
    /// decoding. Keep this aligned with the protection provider's plaintext limit.
    /// </summary>
    public int MaximumEnvelopeCiphertextBytes
    {
        get => _maximumEnvelopeCiphertextBytes;
        set => _maximumEnvelopeCiphertextBytes = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
