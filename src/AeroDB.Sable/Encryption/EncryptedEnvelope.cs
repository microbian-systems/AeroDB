using System.Text.Json;

namespace AeroDB.Sable;

/// <summary>
/// Versioned storage envelope for a single encrypted field value.
/// Binary members are encoded as Base64 strings in the SurrealDB object.
/// </summary>
public sealed record EncryptedEnvelope
{
    public const int CurrentFormatVersion = 1;

    public required int Version { get; init; }
    public required EncryptionAlgorithm Algorithm { get; init; }
    public required string ProviderId { get; init; }
    public required string KeyId { get; init; }
    public required KeyWrappingAlgorithm WrappingAlgorithm { get; init; }
    public required string CodecId { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public required byte[] WrappedKeyNonce { get; init; }
    public required byte[] WrappedKeyCiphertext { get; init; }
    public required byte[] WrappedKeyTag { get; init; }

    internal Dictionary<string, object?> ToStorageObject() => new(StringComparer.Ordinal)
    {
        ["v"] = Version,
        ["alg"] = (int)Algorithm,
        ["provider"] = ProviderId,
        ["kid"] = KeyId,
        ["wrap_alg"] = (int)WrappingAlgorithm,
        ["codec"] = CodecId,
        ["nonce"] = Convert.ToBase64String(Nonce),
        ["ciphertext"] = Convert.ToBase64String(Ciphertext),
        ["tag"] = Convert.ToBase64String(Tag),
        ["wrapped_key_nonce"] = Convert.ToBase64String(WrappedKeyNonce),
        ["wrapped_key_ciphertext"] = Convert.ToBase64String(WrappedKeyCiphertext),
        ["wrapped_key_tag"] = Convert.ToBase64String(WrappedKeyTag)
    };

    internal static EncryptedEnvelope Parse(
        object? storageValue,
        int maximumCiphertextBytes = 1024 * 1024)
    {
        if (storageValue is null)
            throw new SableEnvelopeException("Encrypted field value is null.");
        if (maximumCiphertextBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCiphertextBytes));

        try
        {
            var element = storageValue switch
            {
                JsonElement jsonElement => jsonElement,
                _ => JsonSerializer.SerializeToElement(storageValue)
            };

            if (element.ValueKind != JsonValueKind.Object)
                throw new SableEnvelopeException("Encrypted field value is not an envelope object.");

            return new EncryptedEnvelope
            {
                Version = ReadInt32(element, "v"),
                Algorithm = (EncryptionAlgorithm)ReadInt32(element, "alg"),
                ProviderId = ReadString(element, "provider"),
                KeyId = ReadString(element, "kid"),
                WrappingAlgorithm = (KeyWrappingAlgorithm)ReadInt32(element, "wrap_alg"),
                CodecId = ReadString(element, "codec"),
                Nonce = ReadBytes(element, "nonce", 32),
                Ciphertext = ReadBytes(element, "ciphertext", maximumCiphertextBytes),
                Tag = ReadBytes(element, "tag", 32),
                WrappedKeyNonce = ReadBytes(element, "wrapped_key_nonce", 32),
                WrappedKeyCiphertext = ReadBytes(element, "wrapped_key_ciphertext", 64),
                WrappedKeyTag = ReadBytes(element, "wrapped_key_tag", 32)
            };
        }
        catch (SableEncryptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new SableEnvelopeException("Encrypted field envelope is malformed.", ex);
        }
    }

    private static int ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new SableEnvelopeException($"Encrypted field envelope is missing integer member '{name}'.");

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new SableEnvelopeException($"Encrypted field envelope member '{name}' is null.")
            : throw new SableEnvelopeException($"Encrypted field envelope is missing string member '{name}'.");

    private static byte[] ReadBytes(JsonElement element, string name, int maximumDecodedBytes)
    {
        var encoded = ReadString(element, name);
        var maximumEncodedLength = (((long)maximumDecodedBytes + 2) / 3) * 4;
        if (encoded.Length > maximumEncodedLength)
        {
            throw new SableEnvelopeException(
                $"Encrypted field envelope member '{name}' exceeds its maximum encoded size.");
        }

        var decoded = Convert.FromBase64String(encoded);
        if (decoded.Length > maximumDecodedBytes)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            throw new SableEnvelopeException(
                $"Encrypted field envelope member '{name}' exceeds its maximum decoded size.");
        }

        return decoded;
    }
}

/// <summary>A wrapped field data-encryption key and its routing metadata.</summary>
public sealed record WrappedKey
{
    public required KeyWrappingAlgorithm Algorithm { get; init; }
    public required string ProviderId { get; init; }
    public required string KeyId { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
}
