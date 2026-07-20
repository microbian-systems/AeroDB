using System.Buffers.Binary;
using System.Text;

namespace AeroDB.Sable;

internal static class CanonicalEncryptionAad
{
    private static readonly byte[] FieldPayloadDomain = "SABLE-FIELD-AAD"u8.ToArray();
    private static readonly byte[] FieldWrappingDomain = "SABLE-DEK-WRAP"u8.ToArray();
    private static readonly byte[] VaultPayloadDomain = "SABLE-VAULT-SECRET-AAD"u8.ToArray();
    private static readonly byte[] VaultWrappingDomain = "SABLE-VAULT-DEK-WRAP"u8.ToArray();

    internal static byte[] EncodePayload(
        EncryptionContext context,
        EncryptionAlgorithm payloadAlgorithm)
        => Encode(
            GetDomain(context.Purpose, wrapping: false),
            EncryptedEnvelope.CurrentFormatVersion,
            (int)payloadAlgorithm,
            0,
            string.Empty,
            string.Empty,
            context);

    internal static byte[] EncodeWrapping(
        KeyWrappingContext context,
        KeyWrappingAlgorithm wrappingAlgorithm,
        string providerId,
        string keyId)
        => Encode(
            GetDomain(context.EncryptionContext.Purpose, wrapping: true),
            EncryptedEnvelope.CurrentFormatVersion,
            (int)context.PayloadAlgorithm,
            (int)wrappingAlgorithm,
            providerId,
            keyId,
            context.EncryptionContext);

    private static byte[] GetDomain(EncryptionPurpose purpose, bool wrapping) =>
        purpose switch
        {
            EncryptionPurpose.Field when wrapping => FieldWrappingDomain,
            EncryptionPurpose.Field => FieldPayloadDomain,
            EncryptionPurpose.VaultSecret when wrapping => VaultWrappingDomain,
            EncryptionPurpose.VaultSecret => VaultPayloadDomain,
            _ => throw new SableEnvelopeException(
                $"Encryption purpose '{purpose}' is not supported.")
        };

    private static byte[] Encode(
        byte[] domain,
        int formatVersion,
        int payloadAlgorithm,
        int wrappingAlgorithm,
        string providerId,
        string keyId,
        EncryptionContext context)
    {
        var values = new List<string>
        {
            context.Namespace ?? string.Empty,
            context.Database ?? string.Empty,
            context.Table,
            context.RecordId,
            context.TenantId ?? string.Empty,
            context.StorageField,
            context.CodecId,
            providerId,
            keyId
        };
        if (context.AuthenticatedContext is not null)
            values.Add(context.AuthenticatedContext);

        var encoded = values.Select(Encoding.UTF8.GetBytes).ToArray();
        var length = domain.Length + (3 * sizeof(int))
            + encoded.Sum(value => sizeof(int) + value.Length);
        var result = new byte[length];
        var offset = 0;

        domain.CopyTo(result, offset);
        offset += domain.Length;
        WriteInt32(result, ref offset, formatVersion);
        WriteInt32(result, ref offset, payloadAlgorithm);
        WriteInt32(result, ref offset, wrappingAlgorithm);

        foreach (var value in encoded)
        {
            WriteInt32(result, ref offset, value.Length);
            value.CopyTo(result, offset);
            offset += value.Length;
        }

        return result;
    }

    private static void WriteInt32(byte[] destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, sizeof(int)), value);
        offset += sizeof(int);
    }
}
