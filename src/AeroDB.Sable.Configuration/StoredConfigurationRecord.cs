using System.Text.Json.Serialization;
using Dahomey.Cbor.Attributes;
using SurrealDb.Net.Models;

namespace AeroDB.Sable.Configuration;

internal sealed class StoredConfigurationRecord : Record
{
    [CborProperty("key")]
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [CborProperty("value")]
    [JsonPropertyName("value")]
    public StoredEncryptedEnvelope Value { get; set; } = new();

    [CborProperty("updated_at")]
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

internal sealed class StoredEncryptedEnvelope
{
    [CborProperty("v")]
    [JsonPropertyName("v")]
    public int Version { get; set; }

    [CborProperty("alg")]
    [JsonPropertyName("alg")]
    public int Algorithm { get; set; }

    [CborProperty("provider")]
    [JsonPropertyName("provider")]
    public string ProviderId { get; set; } = string.Empty;

    [CborProperty("kid")]
    [JsonPropertyName("kid")]
    public string KeyId { get; set; } = string.Empty;

    [CborProperty("wrap_alg")]
    [JsonPropertyName("wrap_alg")]
    public int WrappingAlgorithm { get; set; }

    [CborProperty("codec")]
    [JsonPropertyName("codec")]
    public string CodecId { get; set; } = string.Empty;

    [CborProperty("nonce")]
    [JsonPropertyName("nonce")]
    public byte[] Nonce { get; set; } = [];

    [CborProperty("ciphertext")]
    [JsonPropertyName("ciphertext")]
    public byte[] Ciphertext { get; set; } = [];

    [CborProperty("tag")]
    [JsonPropertyName("tag")]
    public byte[] Tag { get; set; } = [];

    [CborProperty("wrapped_key_nonce")]
    [JsonPropertyName("wrapped_key_nonce")]
    public byte[] WrappedKeyNonce { get; set; } = [];

    [CborProperty("wrapped_key_ciphertext")]
    [JsonPropertyName("wrapped_key_ciphertext")]
    public byte[] WrappedKeyCiphertext { get; set; } = [];

    [CborProperty("wrapped_key_tag")]
    [JsonPropertyName("wrapped_key_tag")]
    public byte[] WrappedKeyTag { get; set; } = [];

    internal static StoredEncryptedEnvelope FromEnvelope(EncryptedEnvelope envelope) =>
        new()
        {
            Version = envelope.Version,
            Algorithm = (int)envelope.Algorithm,
            ProviderId = envelope.ProviderId,
            KeyId = envelope.KeyId,
            WrappingAlgorithm = (int)envelope.WrappingAlgorithm,
            CodecId = envelope.CodecId,
            Nonce = envelope.Nonce,
            Ciphertext = envelope.Ciphertext,
            Tag = envelope.Tag,
            WrappedKeyNonce = envelope.WrappedKeyNonce,
            WrappedKeyCiphertext = envelope.WrappedKeyCiphertext,
            WrappedKeyTag = envelope.WrappedKeyTag
        };

    internal EncryptedEnvelope ToEnvelope() =>
        new()
        {
            Version = Version,
            Algorithm = (EncryptionAlgorithm)Algorithm,
            ProviderId = ProviderId,
            KeyId = KeyId,
            WrappingAlgorithm = (KeyWrappingAlgorithm)WrappingAlgorithm,
            CodecId = CodecId,
            Nonce = Nonce,
            Ciphertext = Ciphertext,
            Tag = Tag,
            WrappedKeyNonce = WrappedKeyNonce,
            WrappedKeyCiphertext = WrappedKeyCiphertext,
            WrappedKeyTag = WrappedKeyTag
        };
}
