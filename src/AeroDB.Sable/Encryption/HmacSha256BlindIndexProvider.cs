using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace AeroDB.Sable;

/// <summary>
/// In-process HMAC-SHA-256 blind-index provider with active and previous key
/// versions. Supplied key arrays are copied and zeroed when this provider is disposed.
/// </summary>
public sealed class HmacSha256BlindIndexProvider : IBlindIndexTokenProvider, IDisposable
{
    private static readonly byte[] Domain = "AERODB-SABLE-BLIND-INDEX-V1"u8.ToArray();
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly string[] _queryKeyIds;
    private bool _disposed;

    /// <summary>
    /// Creates a provider. Previous keys remain queryable but are never used for
    /// new storage tokens.
    /// </summary>
    public HmacSha256BlindIndexProvider(
        string activeKeyId,
        byte[] activeKey,
        IReadOnlyDictionary<string, byte[]>? previousKeys = null)
    {
        ValidateKeyId(activeKeyId);
        AddKey(activeKeyId, activeKey);
        ActiveKeyId = activeKeyId;

        if (previousKeys is not null)
        {
            if (previousKeys.Count > 1)
            {
                throw new ArgumentException(
                    "Blind-index rotation supports only the immediately previous key version.",
                    nameof(previousKeys));
            }

            foreach (var previous in previousKeys)
            {
                ValidateKeyId(previous.Key);
                if (string.Equals(previous.Key, activeKeyId, StringComparison.Ordinal))
                    throw new ArgumentException(
                        "A previous blind-index key cannot reuse the active key ID.",
                        nameof(previousKeys));
                AddKey(previous.Key, previous.Value);
            }
        }

        _queryKeyIds = [activeKeyId, .. _keys.Keys.Where(keyId =>
            !string.Equals(keyId, activeKeyId, StringComparison.Ordinal))];
    }

    /// <summary>The key ID used for newly stored tokens.</summary>
    public string ActiveKeyId { get; }

    public ValueTask<string> CreateStorageTokenAsync(
        ReadOnlyMemory<byte> normalizedValue,
        BlindIndexContext context,
        BlindIndexAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return ValueTask.FromResult(CreateToken(ActiveKeyId, normalizedValue.Span, context, algorithm));
    }

    public ValueTask<IReadOnlyList<string>> CreateQueryTokensAsync(
        ReadOnlyMemory<byte> normalizedValue,
        BlindIndexContext context,
        BlindIndexAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        IReadOnlyList<string> tokens = _queryKeyIds
            .Select(keyId => CreateToken(keyId, normalizedValue.Span, context, algorithm))
            .ToArray();
        return ValueTask.FromResult(tokens);
    }

    private string CreateToken(
        string keyId,
        ReadOnlySpan<byte> normalizedValue,
        BlindIndexContext context,
        BlindIndexAlgorithm algorithm)
    {
        if (algorithm != BlindIndexAlgorithm.HmacSha256)
            throw new SableBlindIndexConfigurationException(
                $"Blind-index algorithm '{algorithm}' is not supported.");

        var input = new ArrayBufferWriter<byte>();
        WriteComponent(input, Domain);
        WriteInt32(input, 1);
        WriteInt32(input, (int)algorithm);
        WriteInt32(input, (int)context.Normalizer);
        WriteComponent(input, Encoding.UTF8.GetBytes(keyId));
        WriteNullableComponent(input, context.Namespace);
        WriteNullableComponent(input, context.Database);
        WriteComponent(input, Encoding.UTF8.GetBytes(context.Table));
        WriteNullableComponent(input, context.TenantId);
        WriteComponent(input, Encoding.UTF8.GetBytes(context.StorageField));
        WriteComponent(input, normalizedValue);

        Span<byte> mac = stackalloc byte[32];
        try
        {
            HMACSHA256.HashData(_keys[keyId], input.WrittenSpan, mac);
            return $"bidx:v1:{Base64Url(Encoding.UTF8.GetBytes(keyId))}:{Base64Url(mac)}";
        }
        finally
        {
            if (MemoryMarshal.TryGetArray(
                    input.WrittenMemory,
                    out ArraySegment<byte> inputSegment))
            {
                CryptographicOperations.ZeroMemory(inputSegment.AsSpan());
            }
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static void WriteComponent(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        var destination = writer.GetSpan(sizeof(int) + value.Length);
        BinaryPrimitives.WriteInt32BigEndian(destination, value.Length);
        value.CopyTo(destination[sizeof(int)..]);
        writer.Advance(sizeof(int) + value.Length);
    }

    private static void WriteNullableComponent(ArrayBufferWriter<byte> writer, string? value)
    {
        var presence = writer.GetSpan(1);
        presence[0] = value is null ? (byte)0 : (byte)1;
        writer.Advance(1);
        if (value is not null)
            WriteComponent(writer, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    private void AddKey(string keyId, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
            throw new ArgumentException(
                "Blind-index keys must contain at least 32 bytes of cryptographic key material.",
                nameof(key));
        if (!_keys.TryAdd(keyId, key.ToArray()))
            throw new ArgumentException($"Duplicate blind-index key ID '{keyId}'.", nameof(keyId));
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Blind-index key ID cannot be empty.", nameof(keyId));
        if (Encoding.UTF8.GetByteCount(keyId) > 128
            || keyId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Blind-index key ID must be at most 128 UTF-8 bytes and cannot contain control characters.",
                nameof(keyId));
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var key in _keys.Values)
            CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }
}
