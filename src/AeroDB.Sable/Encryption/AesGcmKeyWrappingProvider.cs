using System.Security.Cryptography;

namespace AeroDB.Sable;

/// <summary>
/// Sable-owned AES-256-GCM DEK-wrapping provider backed by a 256-bit KEK.
/// </summary>
public sealed class AesGcmKeyWrappingProvider : IKeyWrappingProvider, IDisposable
{
    private byte[]? _keyEncryptionKey;

    public AesGcmKeyWrappingProvider(
        ReadOnlySpan<byte> keyEncryptionKey,
        string keyId,
        string providerId = "local")
    {
        if (keyEncryptionKey.Length != 32)
            throw new ArgumentException("AES-256-GCM key wrapping requires a 32-byte KEK.", nameof(keyEncryptionKey));
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("A key ID is required.", nameof(keyId));
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("A provider ID is required.", nameof(providerId));
        if (!AesGcm.IsSupported)
            throw new PlatformNotSupportedException("AES-GCM is not supported on this platform.");

        _keyEncryptionKey = keyEncryptionKey.ToArray();
        KeyId = keyId;
        ProviderId = providerId;
    }

    public string ProviderId { get; }
    public string KeyId { get; }
    public KeyWrappingAlgorithm Algorithm => KeyWrappingAlgorithm.SableAes256Gcm;

    /// <summary>
    /// Loads a raw 32-byte or Base64-encoded 32-byte KEK from a mounted file.
    /// The host remains responsible for the file's ownership and ACL.
    /// </summary>
    public static AesGcmKeyWrappingProvider FromMountedFile(
        string path,
        string keyId,
        string providerId = "mounted-file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileBytes = File.ReadAllBytes(path);
        try
        {
            if (fileBytes.Length == 32)
                return new AesGcmKeyWrappingProvider(fileBytes, keyId, providerId);

            var text = System.Text.Encoding.UTF8.GetString(fileBytes).Trim();
            var decoded = Convert.FromBase64String(text);
            try
            {
                return new AesGcmKeyWrappingProvider(decoded, keyId, providerId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
        }
    }

    public ValueTask<WrappedKey> WrapAsync(
        ReadOnlyMemory<byte> keyMaterial,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kek = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[keyMaterial.Length];
        var tag = new byte[16];
        var aad = CanonicalEncryptionAad.EncodeWrapping(context, Algorithm, ProviderId, KeyId);

        using var aes = new AesGcm(kek, tag.Length);
        aes.Encrypt(nonce, keyMaterial.Span, ciphertext, tag, aad);

        return ValueTask.FromResult(new WrappedKey
        {
            Algorithm = Algorithm,
            ProviderId = ProviderId,
            KeyId = KeyId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag
        });
    }

    public ValueTask<OwnedKeyMaterial> UnwrapAsync(
        WrappedKey wrappedKey,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (wrappedKey.Algorithm != Algorithm
            || !string.Equals(wrappedKey.ProviderId, ProviderId, StringComparison.Ordinal)
            || !string.Equals(wrappedKey.KeyId, KeyId, StringComparison.Ordinal))
        {
            throw new SableEnvelopeException(
                $"Wrapped key routing metadata does not match provider '{ProviderId}' and key '{KeyId}'.");
        }

        if (wrappedKey.Nonce.Length != 12 || wrappedKey.Tag.Length != 16 || wrappedKey.Ciphertext.Length != 32)
            throw new SableEnvelopeException("Wrapped DEK has invalid AES-256-GCM sizes.");

        var plaintext = new byte[wrappedKey.Ciphertext.Length];
        var aad = CanonicalEncryptionAad.EncodeWrapping(context, Algorithm, ProviderId, KeyId);

        try
        {
            using var aes = new AesGcm(GetKey(), wrappedKey.Tag.Length);
            aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext, aad);
            return ValueTask.FromResult(new OwnedKeyMaterial(plaintext));
        }
        catch (AuthenticationTagMismatchException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SableEncryptionAuthenticationException("Wrapped DEK authentication failed.", ex);
        }
    }

    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _keyEncryptionKey, null);
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
    }

    private byte[] GetKey() =>
        _keyEncryptionKey ?? throw new ObjectDisposedException(nameof(AesGcmKeyWrappingProvider));
}
