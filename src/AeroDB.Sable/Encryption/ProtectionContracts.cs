using System.Security.Cryptography;

namespace AeroDB.Sable;

/// <summary>Protects and unprotects field values without exposing DEKs to callers.</summary>
public interface ISableDataProtectionProvider
{
    ValueTask<EncryptedEnvelope> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        EncryptionContext context,
        EncryptionAlgorithm algorithm = EncryptionAlgorithm.Aes256Gcm,
        CancellationToken cancellationToken = default);

    ValueTask<OwnedPlaintext> UnprotectAsync(
        EncryptedEnvelope envelope,
        EncryptionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Wraps and unwraps per-value DEKs.</summary>
public interface IKeyWrappingProvider
{
    string ProviderId { get; }
    string KeyId { get; }
    KeyWrappingAlgorithm Algorithm { get; }

    ValueTask<WrappedKey> WrapAsync(
        ReadOnlyMemory<byte> keyMaterial,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default);

    ValueTask<OwnedKeyMaterial> UnwrapAsync(
        WrappedKey wrappedKey,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Owned plaintext memory that is zeroed on disposal.</summary>
public sealed class OwnedPlaintext : IDisposable
{
    private byte[]? _bytes;

    internal OwnedPlaintext(byte[] bytes)
    {
        _bytes = bytes;
    }

    public ReadOnlyMemory<byte> Memory =>
        _bytes ?? throw new ObjectDisposedException(nameof(OwnedPlaintext));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}

/// <summary>Owned key memory that is zeroed on disposal.</summary>
public sealed class OwnedKeyMaterial : IDisposable
{
    private byte[]? _bytes;

    internal OwnedKeyMaterial(byte[] bytes)
    {
        _bytes = bytes;
    }

    public ReadOnlyMemory<byte> Memory =>
        _bytes ?? throw new ObjectDisposedException(nameof(OwnedKeyMaterial));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}
