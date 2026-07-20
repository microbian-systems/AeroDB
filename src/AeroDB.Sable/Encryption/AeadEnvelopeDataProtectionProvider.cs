using System.Security.Cryptography;

namespace AeroDB.Sable;

/// <summary>
/// AEAD per-value envelope protection with a fresh 256-bit DEK and nonce for each write.
/// </summary>
public class AeadEnvelopeDataProtectionProvider : ISableDataProtectionProvider
{
    private readonly IKeyWrappingProvider _keyWrappingProvider;
    private readonly int _maximumPlaintextBytes;

    public AeadEnvelopeDataProtectionProvider(
        IKeyWrappingProvider keyWrappingProvider,
        int maximumPlaintextBytes = 1024 * 1024)
    {
        _keyWrappingProvider = keyWrappingProvider ?? throw new ArgumentNullException(nameof(keyWrappingProvider));
        if (maximumPlaintextBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));
        _maximumPlaintextBytes = maximumPlaintextBytes;
    }

    public async ValueTask<EncryptedEnvelope> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        EncryptionContext context,
        EncryptionAlgorithm algorithm = EncryptionAlgorithm.Aes256Gcm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAlgorithm(algorithm);

        if (plaintext.Length > _maximumPlaintextBytes)
            throw new SableEncryptionException(
                $"Encrypted field plaintext length {plaintext.Length} exceeds the configured maximum {_maximumPlaintextBytes}.");

        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrappingContext = new KeyWrappingContext(context, algorithm);
            var wrappedKey = await _keyWrappingProvider
                .WrapAsync(dek, wrappingContext, cancellationToken)
                .ConfigureAwait(false);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            var aad = CanonicalEncryptionAad.EncodePayload(
                context,
                algorithm);

            EncryptPayload(algorithm, dek, nonce, plaintext.Span, ciphertext, tag, aad);

            return new EncryptedEnvelope
            {
                Version = EncryptedEnvelope.CurrentFormatVersion,
                Algorithm = algorithm,
                ProviderId = wrappedKey.ProviderId,
                KeyId = wrappedKey.KeyId,
                WrappingAlgorithm = wrappedKey.Algorithm,
                CodecId = context.CodecId,
                Nonce = nonce,
                Ciphertext = ciphertext,
                Tag = tag,
                WrappedKeyNonce = wrappedKey.Nonce,
                WrappedKeyCiphertext = wrappedKey.Ciphertext,
                WrappedKeyTag = wrappedKey.Tag
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async ValueTask<OwnedPlaintext> UnprotectAsync(
        EncryptedEnvelope envelope,
        EncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelope(envelope, context);

        var wrappingContext = new KeyWrappingContext(context, envelope.Algorithm);
        var wrappedKey = new WrappedKey
        {
            Algorithm = envelope.WrappingAlgorithm,
            ProviderId = envelope.ProviderId,
            KeyId = envelope.KeyId,
            Nonce = envelope.WrappedKeyNonce,
            Ciphertext = envelope.WrappedKeyCiphertext,
            Tag = envelope.WrappedKeyTag
        };

        using var dek = await _keyWrappingProvider
            .UnwrapAsync(wrappedKey, wrappingContext, cancellationToken)
            .ConfigureAwait(false);

        var plaintext = new byte[envelope.Ciphertext.Length];
        var aad = CanonicalEncryptionAad.EncodePayload(
            context,
            envelope.Algorithm);

        try
        {
            DecryptPayload(
                envelope.Algorithm,
                dek.Memory.Span,
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                plaintext,
                aad);
            return new OwnedPlaintext(plaintext);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SableEncryptionAuthenticationException("Encrypted field authentication failed.", ex);
        }
    }

    /// <summary>
    /// Rewraps an envelope's existing DEK with the active wrapping provider.
    /// Payload ciphertext, nonce, and authentication tag remain unchanged.
    /// </summary>
    public async ValueTask<EncryptedEnvelope> RewrapAsync(
        EncryptedEnvelope envelope,
        EncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelope(envelope, context);

        var wrappingContext = new KeyWrappingContext(context, envelope.Algorithm);
        var existing = new WrappedKey
        {
            Algorithm = envelope.WrappingAlgorithm,
            ProviderId = envelope.ProviderId,
            KeyId = envelope.KeyId,
            Nonce = envelope.WrappedKeyNonce,
            Ciphertext = envelope.WrappedKeyCiphertext,
            Tag = envelope.WrappedKeyTag
        };
        using var dek = await _keyWrappingProvider
            .UnwrapAsync(existing, wrappingContext, cancellationToken)
            .ConfigureAwait(false);
        var replacement = await _keyWrappingProvider
            .WrapAsync(dek.Memory, wrappingContext, cancellationToken)
            .ConfigureAwait(false);

        return envelope with
        {
            ProviderId = replacement.ProviderId,
            KeyId = replacement.KeyId,
            WrappingAlgorithm = replacement.Algorithm,
            WrappedKeyNonce = replacement.Nonce,
            WrappedKeyCiphertext = replacement.Ciphertext,
            WrappedKeyTag = replacement.Tag
        };
    }

    private static void ValidateAlgorithm(EncryptionAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case EncryptionAlgorithm.Aes256Gcm when AesGcm.IsSupported:
            case EncryptionAlgorithm.ChaCha20Poly1305 when ChaCha20Poly1305.IsSupported:
                return;
            case EncryptionAlgorithm.Aes256Gcm:
            case EncryptionAlgorithm.ChaCha20Poly1305:
                throw new PlatformNotSupportedException(
                    $"Encryption algorithm '{algorithm}' is not supported on this platform.");
            default:
                throw new SableEnvelopeException(
                    $"Encryption algorithm '{algorithm}' is not supported.");
        }
    }

    private void ValidateEnvelope(EncryptedEnvelope envelope, EncryptionContext context)
    {
        if (envelope.Version != EncryptedEnvelope.CurrentFormatVersion)
            throw new SableEnvelopeException($"Unsupported encrypted envelope version '{envelope.Version}'.");
        ValidateAlgorithm(envelope.Algorithm);
        if (envelope.WrappingAlgorithm != KeyWrappingAlgorithm.SableAes256Gcm)
            throw new SableEnvelopeException($"Unsupported key-wrapping algorithm '{envelope.WrappingAlgorithm}'.");
        if (!string.Equals(envelope.CodecId, context.CodecId, StringComparison.Ordinal))
            throw new SableEnvelopeException(
                $"Envelope codec '{envelope.CodecId}' does not match mapped codec '{context.CodecId}'.");
        if (envelope.Nonce.Length != 12 || envelope.Tag.Length != 16)
            throw new SableEnvelopeException(
                $"Encrypted field has invalid {GetDisplayName(envelope.Algorithm)} nonce or tag size.");
        if (envelope.Ciphertext.Length > _maximumPlaintextBytes)
        {
            throw new SableEnvelopeException(
                $"Encrypted field ciphertext exceeds the configured maximum {_maximumPlaintextBytes}.");
        }
    }

    private static void EncryptPayload(
        EncryptionAlgorithm algorithm,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> aad)
    {
        ValidateAlgorithm(algorithm);
        if (algorithm == EncryptionAlgorithm.Aes256Gcm)
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return;
        }

        using var chaCha = new ChaCha20Poly1305(key);
        chaCha.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private static void DecryptPayload(
        EncryptionAlgorithm algorithm,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> aad)
    {
        ValidateAlgorithm(algorithm);
        if (algorithm == EncryptionAlgorithm.Aes256Gcm)
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return;
        }

        using var chaCha = new ChaCha20Poly1305(key);
        chaCha.Decrypt(nonce, ciphertext, tag, plaintext, aad);
    }

    private static string GetDisplayName(EncryptionAlgorithm algorithm)
        => algorithm switch
        {
            EncryptionAlgorithm.Aes256Gcm => "AES-256-GCM",
            EncryptionAlgorithm.ChaCha20Poly1305 => "ChaCha20-Poly1305",
            _ => algorithm.ToString()
        };
}

/// <summary>
/// Compatibility name for the AES-default configuration. New code that selects
/// algorithms per field should use <see cref="AeadEnvelopeDataProtectionProvider"/>.
/// </summary>
public sealed class AesGcmDataProtectionProvider : AeadEnvelopeDataProtectionProvider
{
    public AesGcmDataProtectionProvider(
        IKeyWrappingProvider keyWrappingProvider,
        int maximumPlaintextBytes = 1024 * 1024)
        : base(keyWrappingProvider, maximumPlaintextBytes)
    {
    }
}
